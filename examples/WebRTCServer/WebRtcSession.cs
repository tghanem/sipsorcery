﻿//-----------------------------------------------------------------------------
// Filename: WebRtcSession.cs
//
// Description: Represents a WebRTC session with a remote peer.
//
// History:
// 04 Mar 2016	Aaron Clauson	Created.
// 25 Aug 2019  Aaron Clauson   Updated from video only to audio and video.
// 18 Jan 2020  Aaron Clauson   Combined WebRTCPeer and WebRTCSession.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;
using SIPSorceryMedia;

namespace SIPSorcery.Net
{
    public class WebRtcSession
    {
        private const int PAYLOAD_TYPE_ID = 100;
        private const int ICE_GATHERING_TIMEOUT_MILLISECONDS = 5000;
        private const int INITIAL_STUN_BINDING_PERIOD_MILLISECONDS = 5000;       // The period to send the initial STUN requests used to get an ICE candidates public IP address.
        private const int INITIAL_STUN_BINDING_ATTEMPTS_LIMIT = 3;              // The maximum number of binding attempts to determine a local socket's public IP address before giving up.
        private const int ESTABLISHED_STUN_BINDING_PERIOD_MILLISECONDS = 15000;  // The period to send STUN binding requests to remote peers once the ICE gathering stage is complete.
        private const int COMMUNICATION_FAILURE_COUNT_FOR_CLOSE = 20;           // If a peer gets this number of communication failures on a socket it will close the peer.
        private const int MAXIMUM_TURN_ALLOCATE_ATTEMPTS = 4;
        private const int MAXIMUM_STUN_CONNECTION_ATTEMPTS = 5;
        private const int ICE_TIMEOUT_SECONDS = 5;                              // If no response is received to the STUN connectivity check within this number of seconds the WebRTC connection will be assumed to be broken.
        private const int CLOSE_SOCKETS_TIMEOUT_WAIT_MILLISECONDS = 3000;
        private const string RTP_MEDIA_SECURE_DESCRIPTOR = "RTP/SAVPF";
        private const string RTP_MEDIA_UNSECURE_DESCRIPTOR = "RTP/AVP";
        private const int VP8_PAYLOAD_TYPE_ID = 100;
        private const int DTLS_READ_BUFFER_LENGTH = 8192;

        private static string _sdpOfferTemplate = @"v=0
o=- {0} 2 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE audio video
";
        private static string _sdpAudioPcmOfferTemplate =
    @"m=audio {0} {1} 0
c=IN IP4 {2}
{3}
a=end-of-candidates 
a=ice-ufrag:{4}
a=ice-pwd:{5}{6}
a=setup:actpass
a=sendonly
a=rtcp-mux
a=mid:audio
a=rtpmap:0 PCMU/8000
";

        private static string _sdpVideoOfferTemplate =
    "m=video 0 {0} " + PAYLOAD_TYPE_ID + @"
c=IN IP4 {1}
a=ice-ufrag:{2}
a=ice-pwd:{3}{4}
a=bundle-only 
a=setup:actpass
a=sendonly
a=rtcp-mux
a=mid:video
a=rtpmap:" + PAYLOAD_TYPE_ID + @" VP8/90000
";

        private static string _dtlsFingerprint = "\na=fingerprint:sha-256 {0}";

        private static ILogger logger = Log.Logger;

        private static ManualResetEvent _secureChannelInitMre = new ManualResetEvent(false);

        public string SessionID { get; private set; }
        public string SDP;
        public string SdpSessionID;
        public string LocalIceUser;
        public string LocalIcePassword;
        public string RemoteIceUser;
        public string RemoteIcePassword;
        public bool IsDtlsNegotiationComplete;
        public DateTime IceNegotiationStartedAt;
        public List<IceCandidate> LocalIceCandidates;
        public bool IsClosed;
        public IceConnectionStatesEnum IceConnectionState = IceConnectionStatesEnum.None;

        private List<IceCandidate> _remoteIceCandidates = new List<IceCandidate>();
        public List<IceCandidate> RemoteIceCandidates
        {
            get { return _remoteIceCandidates; }
        }

        public bool IsConnected
        {
            get { return IceConnectionState == IceConnectionStatesEnum.Connected; }
        }

        /// <summary>
        /// The raison d'etre of the ICE checks. This represents the end point
        /// that we were able to connect to for the WebRTC session.
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; private set; }

        RTPChannel _rtpChannel;
        private string _dtlsCertificateFingerprint;
        private IPEndPoint _turnServerEndPoint;
        private ManualResetEvent _iceGatheringMRE;
        private int _communicationFailureCount = 0;
        private List<IPAddress> _offerAddresses;            // If set restricts which local IP addresses will be offered in ICE candidates.
        private int _audioSessionID = 0;
        private int _videoSessionID;
        private DateTime _lastStunSentAt = DateTime.MinValue;
        private DateTime _lastStunMessageReceivedAt = DateTime.MinValue;
        private DateTime _lastCommunicationAt = DateTime.MinValue;

        public event Action<string> OnClose;
        public event Action<string> OnSdpOfferReady;
        //public event Action<IceConnectionStatesEnum> OnIceStateChange;
        //public event Action<IceCandidate, byte[], IPEndPoint> OnDtlsPacket;
        //public event Action<IceCandidate, byte[], IPEndPoint> OnMediaPacket;
        //public event Action<IceCandidate, IPEndPoint> OnIceConnected;

        public DtlsManaged DtlsContext;
        public SRTPManaged SrtpContext;
        public SRTPManaged SrtpReceiveContext;  // Used to decrypt packets received from the remote peer.
        public RTPSession _rtpSession;

        //public RTPSession _videoRtpSession;

        public bool IsEncryptionDisabled { get; private set; }

        private string _dtlsCertFilePath;
        private string _dtlsKeyFilePath;

        public WebRtcSession(string dtlsCertFilePath, string dtlsKeyFilePath, string dtlsFingerprint, List<IPAddress> offerAddresses)
        {
            _dtlsCertFilePath = dtlsCertFilePath;
            _dtlsKeyFilePath = dtlsKeyFilePath;
            _dtlsCertificateFingerprint = dtlsFingerprint;

            SessionID = Guid.NewGuid().ToString();

            _rtpSession = new RTPSession((int)SDPMediaFormatsEnum.PCMU, AddressFamily.InterNetwork, true, null);
            _videoSessionID = _rtpSession.AddStream(VP8_PAYLOAD_TYPE_ID, null);
            _rtpChannel = _rtpSession.RtpChannel;
            _rtpChannel.OnRTPDataReceived += OnRTPDataReceived;
            _rtpSession.OnRtpClosed += (reason) => { Close(); OnClose?.Invoke(reason); };

            _offerAddresses = offerAddresses;

            // TODO: Set control endpoint to remote RTP end point for RTCP reports.
        }

        /// <summary>
        /// From RFC5764:
        ///             +----------------+
        ///             | 127 < B< 192  -+--> forward to RTP
        ///             |                |
        /// packet -->  |  19 < B< 64   -+--> forward to DTLS
        ///             |                |
        ///             |       B< 2    -+--> forward to STUN
        ///             +----------------+
        /// </summary>
        /// <param name="remoteEP"></param>
        /// <param name="buffer"></param>
        private void OnRTPDataReceived(IPEndPoint remoteEP, byte[] buffer)
        {
            //logger.LogDebug($"RTP channel received a packet from {remoteEP}, {buffer?.Length} bytes.");

            if (buffer?.Length > 0)
            {
                _lastCommunicationAt = DateTime.Now;

                try
                {
                    if (buffer[0] == 0x00 || buffer[0] == 0x01)
                    {
                        // STUN packet.
                        _lastStunMessageReceivedAt = DateTime.Now;
                        var stunMessage = STUNv2Message.ParseSTUNMessage(buffer, buffer.Length);
                        ProcessStunMessage(stunMessage, remoteEP);
                    }
                    else if (buffer[0] >= 128 && buffer[0] <= 191)
                    {
                        // RTP/RTCP packet.
                        // Don't need to do anything here, these get handled by the RTPSession.
                    }
                    else if (buffer[0] >= 20 && buffer[0] <= 63)
                    {
                        // DTLS packet.
                        DtlsPacketReceived(remoteEP, buffer);
                    }
                    else
                    {
                        logger.LogWarning("Unknown packet type received on RTP channel.");
                    }
                }
                catch (Exception excp)
                {
                    logger.LogError($"Exception WebRtcSession.OnRTPDataReceived {excp.Message}.");
                }
            }
        }

        public void Close()
        {
            // TODO.
            IsClosed = true;
        }

        private void DtlsPacketReceived(IPEndPoint remoteEndPoint, byte[] buffer)
        {
            logger.LogDebug("DTLS packet received of " + buffer.Length + " bytes from " + remoteEndPoint.ToString() + ".");

            if (!File.Exists(_dtlsCertFilePath))
            {
                throw new ApplicationException($"The DTLS certificate file could not be found at {_dtlsCertFilePath}.");
            }

            if (!File.Exists(_dtlsKeyFilePath))
            {
                throw new ApplicationException($"The DTLS key file could not be found at {_dtlsKeyFilePath}.");
            }

            if (DtlsContext == null)
            {
                lock (_secureChannelInitMre)
                {
                    DtlsContext = new DtlsManaged(_dtlsCertFilePath, _dtlsKeyFilePath);
                    int res = DtlsContext.Init();
                    logger.LogDebug("DtlsContext initialisation result=" + res);
                }
            }

            int bytesWritten = DtlsContext.Write(buffer, buffer.Length);

            if (bytesWritten != buffer.Length)
            {
                logger.LogWarning("The required number of bytes were not successfully written to the DTLS context.");
            }
            else
            {
                byte[] dtlsOutBytes = new byte[DTLS_READ_BUFFER_LENGTH];

                int bytesRead = DtlsContext.Read(dtlsOutBytes, dtlsOutBytes.Length);

                if (bytesRead == 0)
                {
                    logger.LogDebug("No bytes read from DTLS context :(.");
                }
                else
                {
                    logger.LogDebug(bytesRead + " bytes read from DTLS context sending to " + remoteEndPoint.ToString() + ".");
                    //iceCandidate.LocalRtpSocket.SendTo(dtlsOutBytes, 0, bytesRead, SocketFlags.None, remoteEndPoint);
                    _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, remoteEndPoint, dtlsOutBytes.Take(bytesRead).ToArray());

                    //if (client.DtlsContext.IsHandshakeComplete())
                    if (DtlsContext.GetState() == 3)
                    {
                        logger.LogDebug("DTLS negotiation complete for " + remoteEndPoint.ToString() + ".");

                        lock (_secureChannelInitMre)
                        {
                            SrtpContext = new SRTPManaged(DtlsContext, false);
                            SrtpReceiveContext = new SRTPManaged(DtlsContext, true);
                        }

                        IsDtlsNegotiationComplete = true;

                        _rtpSession.SrtpProtect = SrtpContext.ProtectRTP;
                        _rtpSession.SrtcpProtect = SrtpContext.ProtectRTCP;
                    }
                }
            }
        }

        public void InitEncryptionDisabledSession(IceCandidate iceCandidate, IPEndPoint remoteEndPoint)
        {
            logger.LogDebug($"Initialising non encrypted WebRtc session for remote end point {remoteEndPoint.ToString()}.");
            RemoteEndPoint = remoteEndPoint;
        }

        public void MediaPacketReceived(IceCandidate iceCandidate, byte[] buffer, IPEndPoint remoteEndPoint)
        {
            if ((buffer[0] >= 128) && (buffer[0] <= 191))
            {
                //logger.Debug("A non-STUN packet was received Receiver Client.");

                if (buffer[1] == 0xC8 /* RTCP SR */ || buffer[1] == 0xC9 /* RTCP RR */)
                {
                    // RTCP packet.
                    //webRtcClient.LastSTUNReceiveAt = DateTime.Now;
                }
                else
                {
                    // RTP packet.
                    //int res = peer.SrtpReceiveContext.UnprotectRTP(buffer, buffer.Length);

                    //if (res != 0)
                    //{
                    //    logger.Warn("SRTP unprotect failed, result " + res + ".");
                    //}
                }
            }
            else
            {
                logger.LogDebug("An unrecognised packet was received on the WebRTC media socket.");
            }
        }

        public void SendMedia(SDPMediaTypesEnum mediaType, uint sampleTimestamp, byte[] sample)
        {
            if (RemoteEndPoint != null)
            {
                if (mediaType == SDPMediaTypesEnum.video)
                {
                    int streamID = _videoSessionID;
                    _rtpSession.DestinationEndPoint = RemoteEndPoint;
                    _rtpSession.SendVp8Frame(sampleTimestamp, sample, streamID);
                }
                else if (mediaType == SDPMediaTypesEnum.audio)
                {
                    int streamID = _audioSessionID;
                    _rtpSession.DestinationEndPoint = RemoteEndPoint;
                    _rtpSession.SendAudioFrame(sampleTimestamp, sample, streamID);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="turnServerEndPoint">An optional parameter that can be used include a TURN server in this peer's ICE candidate gathering.</param>
        /// <param name="localAddress">Optional parameter to specify the local IP address to use for STUN/RTP sockets. If null all available interfaces will be used.</param>
        public void Initialise(IPEndPoint turnServerEndPoint)
        {
            try
            {
                _turnServerEndPoint = turnServerEndPoint;

                _iceGatheringMRE = new ManualResetEvent(false);

                DateTime startGatheringTime = DateTime.Now;

                IceConnectionState = IceConnectionStatesEnum.Gathering;

                GetIceCandidates(_iceGatheringMRE);

                _iceGatheringMRE.WaitOne(ICE_GATHERING_TIMEOUT_MILLISECONDS, true);

                logger.LogDebug("ICE gathering completed for in " + DateTime.Now.Subtract(startGatheringTime).TotalMilliseconds + "ms, number of local sockets " + LocalIceCandidates.Count + ".");

                IceConnectionState = IceConnectionStatesEnum.GatheringComplete;

                if (LocalIceCandidates.Count == 0)
                {
                    logger.LogWarning("No local socket candidates were found for WebRTC call closing.");
                    Close();
                }
                else
                {
                    string localIceCandidateString = null;

                    foreach (var iceCandidate in GetIceCandidates())
                    {
                        localIceCandidateString += iceCandidate.ToString();
                    }

                    LocalIceUser = LocalIceUser ?? Crypto.GetRandomString(20);
                    LocalIcePassword = LocalIcePassword ?? Crypto.GetRandomString(20) + Crypto.GetRandomString(20);
                    //var localIceCandidate = GetIceCandidatesForMediaType(RtpMediaTypesEnum.None).First();

                    var offerHeader = String.Format(_sdpOfferTemplate, Crypto.GetRandomInt(10).ToString());

                    string dtlsAttribute = String.Format(_dtlsFingerprint, _dtlsCertificateFingerprint);
                    string rtpSecurityDescriptor = RTP_MEDIA_SECURE_DESCRIPTOR;

                    var audioOffer = String.Format(_sdpAudioPcmOfferTemplate,
                        _rtpChannel.RTPPort,
                         rtpSecurityDescriptor,
                         IPAddress.Loopback,
                         localIceCandidateString.TrimEnd(),
                         LocalIceUser,
                         LocalIcePassword,
                         dtlsAttribute);

                    var videoOffer = String.Format(_sdpVideoOfferTemplate,
                        rtpSecurityDescriptor,
                        IPAddress.Loopback,
                        LocalIceUser,
                        LocalIcePassword,
                        dtlsAttribute);

                    string offer = offerHeader + audioOffer + videoOffer;

                    //logger.LogDebug("WebRTC Offer SDP: " + offer);

                    SDP = offer;

                    // TODO: Reactivate.
                    //Task.Run(() => { SendStunConnectivityChecks(); });

                    OnSdpOfferReady?.Invoke(offer);
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception WebRtcPeer.Initialise. " + excp);
                Close();
            }
        }

        public void AppendRemoteIceCandidate(IceCandidate remoteIceCandidate)
        {
            IPAddress candidateIPAddress = null;

            //foreach (var iceCandidate in remoteIceCandidates)
            //{
            //    logger.LogDebug("Appending remote ICE candidate " + iceCandidate.NetworkAddress + ":" + iceCandidate.Port + ".");
            //}

            if (remoteIceCandidate.Transport.ToLower() != "udp")
            {
                logger.LogDebug("Omitting remote non-UDP ICE candidate. " + remoteIceCandidate.RawString + ".");
            }
            else if (!IPAddress.TryParse(remoteIceCandidate.NetworkAddress, out candidateIPAddress))
            {
                logger.LogDebug("Omitting ICE candidate with unrecognised IP Address. " + remoteIceCandidate.RawString + ".");
            }
            else if (candidateIPAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                logger.LogDebug("Omitting IPv6 ICE candidate. " + remoteIceCandidate.RawString + ".");
            }
            else
            {
                // ToDo: Add srflx and relay endpoints as hosts as well.

                if (!_remoteIceCandidates.Any(x => x.NetworkAddress == remoteIceCandidate.NetworkAddress && x.Port == remoteIceCandidate.Port))
                {
                    logger.LogDebug("Adding remote ICE candidate: " + remoteIceCandidate.CandidateType + " " + remoteIceCandidate.NetworkAddress + ":" + remoteIceCandidate.Port + " (" + remoteIceCandidate.RawString + ").");
                    _remoteIceCandidates.Add(remoteIceCandidate);
                }
            }
        }

        private List<IceCandidate> GetIceCandidates()
        {
            //List<IceCandidate> candidates = new List<IceCandidate>();

            //foreach (var candidate in LocalIceCandidates)
            //{
            //    var localRtpEndPoint = candidate.LocalRtpSocket.LocalEndPoint as IPEndPoint;

            //    if (localRtpEndPoint.Port >= WEBRTC_START_RTP_PORT && localRtpEndPoint.Port <= WEBRTC_END_RTP_PORT)
            //    {
            //        candidates.Add(candidate);
            //    }
            //}

            //return candidates;

            return LocalIceCandidates;
        }

        private void GetIceCandidates(ManualResetEvent iceGatheringCompleteMRE)
        {
            var localIPAddresses = _offerAddresses ?? NetServices.GetAllLocalIPAddresses();
            IceNegotiationStartedAt = DateTime.Now;
            LocalIceCandidates = new List<IceCandidate>();

            foreach (var address in localIPAddresses.Where(x => x.AddressFamily == _rtpChannel.RTPLocalEndPoint.AddressFamily))
            {
                var iceCandidate = new IceCandidate(address, _rtpChannel.RTPPort, (_turnServerEndPoint != null) ? new TurnServer() { ServerEndPoint = _turnServerEndPoint } : null);

                LocalIceCandidates.Add(iceCandidate);

                //var listenerTask = Task.Run(() => { StartWebRtcRtpListener(iceCandidate); });

                //iceCandidate.RtpListenerTask = listenerTask;

                if (_turnServerEndPoint != null)
                {
                    var stunBindingTask = Task.Run(() => { SendInitialStunBindingRequest(iceCandidate, iceGatheringCompleteMRE); });
                }
                else
                {
                    iceCandidate.IsGatheringComplete = true;

                    // Potentially save a few seconds if all the ICE candidates are now ready.
                    if (LocalIceCandidates.All(x => x.IsGatheringComplete))
                    {
                        iceGatheringCompleteMRE.Set();
                    }
                }
            }
        }

        private void SendInitialStunBindingRequest(IceCandidate iceCandidate, ManualResetEvent iceGatheringCompleteMRE)
        {
            int attempt = 1;

            while (attempt < INITIAL_STUN_BINDING_ATTEMPTS_LIMIT && !IsConnected && !IsClosed && !iceCandidate.IsGatheringComplete)
            {
                logger.LogDebug($"Sending STUN binding request {attempt} from {_rtpChannel.RTPLocalEndPoint} to {iceCandidate.TurnServer.ServerEndPoint}.");

                STUNv2Message stunRequest = new STUNv2Message(STUNv2MessageTypesEnum.BindingRequest);
                stunRequest.Header.TransactionId = Guid.NewGuid().ToByteArray().Take(12).ToArray();
                byte[] stunReqBytes = stunRequest.ToByteBuffer(null, false);

                //iceCandidate.LocalRtpSocket.SendTo(stunReqBytes, iceCandidate.TurnServer.ServerEndPoint);
                _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, iceCandidate.TurnServer.ServerEndPoint, stunReqBytes);

                Thread.Sleep(INITIAL_STUN_BINDING_PERIOD_MILLISECONDS);

                attempt++;
            }

            iceCandidate.IsGatheringComplete = true;

            // Potentially save a few seconds if all the ICE candidates are now ready.
            if (LocalIceCandidates.All(x => x.IsGatheringComplete))
            {
                iceGatheringCompleteMRE.Set();
            }
        }

        private void SendStunConnectivityChecks()
        {
            try
            {
                while (!IsClosed)
                {
                    try
                    {
                        // If one of the ICE candidates has the remote RTP socket set then the negotiation is complete and the STUN checks are to keep the connection alive.
                        if (IsConnected)
                        {
                            // Remote RTP endpoint gets set when the DTLS negotiation is finished.
                            if (RemoteEndPoint != null)
                            {
                                //logger.LogDebug("Sending STUN connectivity check to client " + iceCandidate.RemoteRtpEndPoint + ".");

                                string localUser = LocalIceUser;

                                STUNv2Message stunRequest = new STUNv2Message(STUNv2MessageTypesEnum.BindingRequest);
                                stunRequest.Header.TransactionId = Guid.NewGuid().ToByteArray().Take(12).ToArray();
                                stunRequest.AddUsernameAttribute(RemoteIceUser + ":" + localUser);
                                stunRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.Priority, new byte[] { 0x6e, 0x7f, 0x1e, 0xff }));
                                stunRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.UseCandidate, null));   // Must send this to get DTLS started.
                                byte[] stunReqBytes = stunRequest.ToByteBufferStringKey(RemoteIcePassword, true);

                                _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, RemoteEndPoint, stunReqBytes);

                                _lastStunSentAt = DateTime.Now;
                            }

                            if (_lastCommunicationAt != DateTime.MinValue)
                            {
                                var secondsSinceLastResponse = DateTime.Now.Subtract(_lastCommunicationAt).TotalSeconds;

                                if (secondsSinceLastResponse > ICE_TIMEOUT_SECONDS)
                                {
                                    logger.LogWarning($"No packets have been received from {RemoteEndPoint} within the last {secondsSinceLastResponse:#} seconds.");
                                }
                            }
                        }
                        //else
                        //{
                        //    if (_remoteIceCandidates.Count() > 0)
                        //    {
                        //        foreach (var localIceCandidate in LocalIceCandidates.Where(x => x.IsStunLocalExchangeComplete == false && x.StunConnectionRequestAttempts < MAXIMUM_STUN_CONNECTION_ATTEMPTS))
                        //        {
                        //            localIceCandidate.StunConnectionRequestAttempts++;

                        //            // ToDo: Include srflx and relay addresses.

                        //            // Only supporting UDP candidates at this stage.
                        //            foreach (var remoteIceCandidate in RemoteIceCandidates.Where(x => x.Transport.ToLower() == "udp" && x.NetworkAddress.NotNullOrBlank() && x.HasConnectionError == false))
                        //            {
                        //                try
                        //                {
                        //                    IPAddress remoteAddress = IPAddress.Parse(remoteIceCandidate.NetworkAddress);

                        //                    logger.LogDebug($"Sending authenticated STUN binding request {localIceCandidate.StunConnectionRequestAttempts} from {_rtpChannel.RTPLocalEndPoint} to WebRTC peer at {remoteIceCandidate.NetworkAddress}:{remoteIceCandidate.Port}.");

                        //                    string localUser = LocalIceUser;

                        //                    STUNv2Message stunRequest = new STUNv2Message(STUNv2MessageTypesEnum.BindingRequest);
                        //                    stunRequest.Header.TransactionId = Guid.NewGuid().ToByteArray().Take(12).ToArray();
                        //                    stunRequest.AddUsernameAttribute(RemoteIceUser + ":" + localUser);
                        //                    stunRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.Priority, new byte[] { 0x6e, 0x7f, 0x1e, 0xff }));
                        //                    stunRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.UseCandidate, null));   // Must send this to get DTLS started.
                        //                    byte[] stunReqBytes = stunRequest.ToByteBufferStringKey(RemoteIcePassword, true);

                        //                    //localIceCandidate.LocalRtpSocket.SendTo(stunReqBytes, new IPEndPoint(IPAddress.Parse(remoteIceCandidate.NetworkAddress), remoteIceCandidate.Port));
                        //                    _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, new IPEndPoint(IPAddress.Parse(remoteIceCandidate.NetworkAddress), remoteIceCandidate.Port), stunReqBytes);

                        //                    localIceCandidate.LastSTUNSendAt = DateTime.Now;
                        //                }
                        //                catch (System.Net.Sockets.SocketException sockExcp)
                        //                {
                        //                    logger.LogWarning($"SocketException sending STUN request to {remoteIceCandidate.NetworkAddress}:{remoteIceCandidate.Port}, removing candidate. {sockExcp.Message}");
                        //                    remoteIceCandidate.HasConnectionError = true;
                        //                }
                        //            }
                        //        }
                        //    }
                        //}
                    }
                    catch (Exception excp)
                    {
                        logger.LogError("Exception SendStunConnectivityCheck ConnectivityCheck. " + excp);
                    }

                    if (!IsClosed)
                    {
                        Thread.Sleep(ESTABLISHED_STUN_BINDING_PERIOD_MILLISECONDS);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception SendStunConnectivityCheck. " + excp);
            }
        }

        private void ProcessStunMessage(STUNv2Message stunMessage, IPEndPoint remoteEndPoint)
        {
            //logger.LogDebug("STUN message received from remote " + remoteEndPoint + " " + stunMessage.Header.MessageType + ".");

            if (stunMessage.Header.MessageType == STUNv2MessageTypesEnum.BindingRequest)
            {
                STUNv2Message stunResponse = new STUNv2Message(STUNv2MessageTypesEnum.BindingSuccessResponse);
                stunResponse.Header.TransactionId = stunMessage.Header.TransactionId;
                stunResponse.AddXORMappedAddressAttribute(remoteEndPoint.Address, remoteEndPoint.Port);

                // ToDo: Check authentication.

                string localIcePassword = LocalIcePassword;
                byte[] stunRespBytes = stunResponse.ToByteBufferStringKey(localIcePassword, true);
                //iceCandidate.LocalRtpSocket.SendTo(stunRespBytes, remoteEndPoint);
                _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, remoteEndPoint, stunRespBytes);

                //iceCandidate.LastStunRequestReceivedAt = DateTime.Now;
                //iceCandidate.IsStunRemoteExchangeComplete = true;

                if (RemoteEndPoint == null)
                {
                    RemoteEndPoint = remoteEndPoint; // Don't need to wait for DTLS negotiation.
                    //OnIceConnected?.Invoke(iceCandidate, remoteEndPoint);
                    IceConnectionState = IceConnectionStatesEnum.Connected;
                }

                if (_remoteIceCandidates != null && !_remoteIceCandidates.Any(x =>
                     (x.NetworkAddress == remoteEndPoint.Address.ToString() || x.RemoteAddress == remoteEndPoint.Address.ToString()) &&
                     (x.Port == remoteEndPoint.Port || x.RemotePort == remoteEndPoint.Port)))
                {
                    // This STUN request has come from a socket not in the remote ICE candidates list. Add it so we can send our STUN binding request to it.
                    IceCandidate remoteIceCandidate = new IceCandidate("udp", remoteEndPoint.Address, remoteEndPoint.Port, IceCandidateTypesEnum.host);
                    //{
                    //    Transport = "udp",
                    //    NetworkAddress = remoteEndPoint.Address.ToString(),
                    //    Port = remoteEndPoint.Port,
                    //    CandidateType = IceCandidateTypesEnum.host,
                    //    //MediaType = iceCandidate.MediaType
                    //};

                    logger.LogDebug("Adding missing remote ICE candidate for " + remoteEndPoint + ".");

                    _remoteIceCandidates.Add(remoteIceCandidate);
                }
            }
            else if (stunMessage.Header.MessageType == STUNv2MessageTypesEnum.BindingSuccessResponse)
            {
                // TODO: What needs to be done here?

                //if (_turnServerEndPoint != null && remoteEndPoint.ToString() == _turnServerEndPoint.ToString())
                //{
                //    if (iceCandidate.IsGatheringComplete == false)
                //    {
                //        var reflexAddressAttribute = stunMessage.Attributes.FirstOrDefault(y => y.AttributeType == STUNv2AttributeTypesEnum.XORMappedAddress) as STUNv2XORAddressAttribute;

                //        if (reflexAddressAttribute != null)
                //        {
                //            iceCandidate.StunRflxIPEndPoint = new IPEndPoint(reflexAddressAttribute.Address, reflexAddressAttribute.Port);
                //            iceCandidate.IsGatheringComplete = true;

                //            logger.LogDebug("ICE gathering complete for local socket " + iceCandidate.RtpChannel.RTPLocalEndPoint + ", rflx address " + iceCandidate.StunRflxIPEndPoint + ".");
                //        }
                //        else
                //        {
                //            iceCandidate.IsGatheringComplete = true;

                //            logger.LogDebug("The STUN binding response received on " + iceCandidate.RtpChannel.RTPLocalEndPoint + " from " + remoteEndPoint + " did not have an XORMappedAddress attribute, rlfx address can not be determined.");
                //        }
                //    }
                //}
                //else
                //{
                //    iceCandidate.LastStunResponseReceivedAt = DateTime.Now;

                //    if (iceCandidate.IsStunLocalExchangeComplete == false)
                //    {
                //        iceCandidate.IsStunLocalExchangeComplete = true;
                //        logger.LogDebug("WebRTC client STUN exchange complete for call " + CallID + ", candidate local socket " + iceCandidate.RtpChannel.RTPLocalEndPoint + ", remote socket " + remoteEndPoint + ".");

                //        SetIceConnectionState(IceConnectionStatesEnum.Connected);
                //    }
                //}
            }
            else if (stunMessage.Header.MessageType == STUNv2MessageTypesEnum.BindingErrorResponse)
            {
                logger.LogWarning($"A STUN binding error response was received from {remoteEndPoint}.");
            }
            else
            {
                logger.LogWarning($"An unrecognised STUN request was received from {remoteEndPoint}.");
            }
        }

        private void AllocateTurn(IceCandidate iceCandidate)
        {
            try
            {
                if (iceCandidate.TurnAllocateAttempts >= MAXIMUM_TURN_ALLOCATE_ATTEMPTS)
                {
                    logger.LogDebug("TURN allocation for local socket " + iceCandidate.LocalAddress + " failed after " + iceCandidate.TurnAllocateAttempts + " attempts.");

                    iceCandidate.IsGatheringComplete = true;
                }
                else
                {
                    iceCandidate.TurnAllocateAttempts++;

                    //logger.LogDebug("Sending STUN connectivity check to client " + client.SocketAddress + ".");

                    STUNv2Message stunRequest = new STUNv2Message(STUNv2MessageTypesEnum.Allocate);
                    stunRequest.Header.TransactionId = Guid.NewGuid().ToByteArray().Take(12).ToArray();
                    stunRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.Lifetime, 3600));
                    stunRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.RequestedTransport, STUNv2AttributeConstants.UdpTransportType));   // UDP
                    byte[] stunReqBytes = stunRequest.ToByteBuffer(null, false);
                    //iceCandidate.LocalRtpSocket.SendTo(stunReqBytes, iceCandidate.TurnServer.ServerEndPoint);
                    _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, iceCandidate.TurnServer.ServerEndPoint, stunReqBytes);
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception AllocateTurn. " + excp);
            }
        }

        private void CreateTurnPermissions()
        {
            try
            {
                var localTurnIceCandidate = (from cand in LocalIceCandidates where cand.TurnRelayIPEndPoint != null select cand).First();
                var remoteTurnCandidate = (from cand in RemoteIceCandidates where cand.CandidateType == IceCandidateTypesEnum.relay select cand).First();

                // Send create permission request
                STUNv2Message turnPermissionRequest = new STUNv2Message(STUNv2MessageTypesEnum.CreatePermission);
                turnPermissionRequest.Header.TransactionId = Guid.NewGuid().ToByteArray().Take(12).ToArray();
                //turnBindRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.ChannelNumber, (ushort)3000));
                turnPermissionRequest.Attributes.Add(new STUNv2XORAddressAttribute(STUNv2AttributeTypesEnum.XORPeerAddress, remoteTurnCandidate.Port, IPAddress.Parse(remoteTurnCandidate.NetworkAddress)));
                turnPermissionRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.Username, Encoding.UTF8.GetBytes(localTurnIceCandidate.TurnServer.Username)));
                turnPermissionRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.Nonce, Encoding.UTF8.GetBytes(localTurnIceCandidate.TurnServer.Nonce)));
                turnPermissionRequest.Attributes.Add(new STUNv2Attribute(STUNv2AttributeTypesEnum.Realm, Encoding.UTF8.GetBytes(localTurnIceCandidate.TurnServer.Realm)));

                MD5 md5 = new MD5CryptoServiceProvider();
                byte[] hmacKey = md5.ComputeHash(Encoding.UTF8.GetBytes(localTurnIceCandidate.TurnServer.Username + ":" + localTurnIceCandidate.TurnServer.Realm + ":" + localTurnIceCandidate.TurnServer.Password));

                byte[] turnPermissionReqBytes = turnPermissionRequest.ToByteBuffer(hmacKey, false);
                //localTurnIceCandidate.LocalRtpSocket.SendTo(turnPermissionReqBytes, localTurnIceCandidate.TurnServer.ServerEndPoint);
                _rtpChannel.SendAsync(RTPChannelSocketsEnum.RTP, localTurnIceCandidate.TurnServer.ServerEndPoint, turnPermissionReqBytes);
            }
            catch (Exception excp)
            {
                logger.LogError("Exception CreateTurnPermissions. " + excp);
            }
        }
    }
}
