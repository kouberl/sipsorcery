﻿//-----------------------------------------------------------------------------
// Filename: WebRTCDaemon.cs
//
// Description: This class manages both the web socket and WebRTC connections from external peers.
//
// History:
// 04 Mar 2016	Aaron Clauson	Created.
//
// License: 
// This software is licensed under the BSD License http://www.opensource.org/licenses/bsd-license.php
//
// Copyright (c) 2016 Aaron Clauson (aaron@sipsorcery.com), SIP Sorcery Pty Ltd, Hobart, Australia (www.sipsorcery.com)
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that 
// the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. 
// Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following 
// disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of SIP Sorcery Pty Ltd 
// nor the names of its contributors may be used to endorse or promote products derived from this software without specific 
// prior written permission. 
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, 
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, 
// OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
// OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, 
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
// POSSIBILITY OF SUCH DAMAGE.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using log4net;
using NAudio.Codecs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WebSocketSharp;
using WebSocketSharp.Server;
//using Fleck;

namespace WebRTCVideoServer
{
    public class WebRTCDaemon
    {
        private const float TEXT_SIZE_PERCENTAGE = 0.035f;       // height of text as a percentage of the total image height
        private const float TEXT_OUTLINE_REL_THICKNESS = 0.02f; // Black text outline thickness is set as a percentage of text height in pixels
        private const int TEXT_MARGIN_PIXELS = 5;
        private const int POINTS_PER_INCH = 72;
        private const string LOCAL_IP_ADDRESS = "192.168.11.50";
        //private const string LOCAL_IP_ADDRESS = "127.0.0.1";

        private const string DTLS_CERTIFICATE_THUMBRPINT = "25:5A:A9:32:1F:35:04:8D:5F:8A:5B:27:0B:9F:A2:90:1A:0E:B9:E9:02:A2:24:95:64:E5:7C:4C:10:11:F7:36";

        private static ILog logger = AppState.logger;

        private string _webSocketCertificatePath = AppState.GetConfigSetting("WebSocketCertificatePath");
        private string _webSocketCertificatePassword = AppState.GetConfigSetting("WebSocketCertificatePassword");

        private bool _exit = false;
        private WebSocketServer _receiverWSS;
        private ConcurrentDictionary<string, WebRtcSession> _webRtcSessions = new ConcurrentDictionary<string, WebRtcSession>();

        public void Start()
        {
            try
            {
                logger.Debug("WebRTCDaemon starting.");

                SIPSorceryMedia.MFVideoSampler sampler = new MFVideoSampler();
                sampler.InitFromFile();

                SDPExchangeReceiver.WebSocketOpened += WebRtcStartCall;
                SDPExchangeReceiver.SDPAnswerReceived += WebRtcAnswerReceived;

                //var wssCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(_webSocketCertificatePath, _webSocketCertificatePassword);
                //logger.Debug("Web Socket Server Certificate CN: " + wssCertificate.Subject + ", have key " + wssCertificate.HasPrivateKey + ", Expires " + wssCertificate.GetExpirationDateString() + ".");

                _receiverWSS = new WebSocketServer("ws://192.168.11.50:8081");
                //_receiverWSS.Certificate = wssCertificate;
                //_receiverWSS.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
                //_receiverWSS.Start(socket =>
                //{
                //    socket.OnOpen = () => Console.WriteLine("Open!");
                //    socket.OnClose = () => Console.WriteLine("Close!");
                //    socket.OnMessage = message => socket.Send(message);
                //});

                //_receiverWSS = new WebSocketServer(8081, true);
                _receiverWSS.Log.Level = LogLevel.Debug;
                //_receiverWSS.SslConfiguration = new WebSocketSharp.Net.ServerSslConfiguration(wssCertificate, false,
                //     System.Security.Authentication.SslProtocols.Tls,
                //    false);

                _receiverWSS.AddWebSocketService<SDPExchangeReceiver>("/stream",
                    () => new SDPExchangeReceiver()
                    {
                        IgnoreExtensions = true,
                    });
                _receiverWSS.Start();

                //PlayPcmAudio();
                Task.Run(SendTestPattern);
                //Task.Run(SendMp4);
                //Task.Run(SendMp4ViaFile);
                //Task.Run(SendPcmAudio);
                Task.Run(SendSineWaveAudio);
            }
            catch (Exception excp)
            {
                logger.Error("Exception WebRTCDaemon.Start. " + excp);
            }
        }

        public void Stop()
        {
            try
            {
                logger.Debug("Stopping WebRTCDaemon.");

                _exit = true;

                foreach (var session in _webRtcSessions.Values)
                {
                    session.Peer.Close();
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception WebRTCDaemon.Stop. " + excp);
            }
        }

        private void WebRtcStartCall(WebSocketSharp.Net.WebSockets.WebSocketContext context, string webSocketID)
        {
            logger.Debug("New WebRTC client added for web socket connection " + webSocketID + ".");

            var mediaTypes = new List<RtpMediaTypesEnum> { RtpMediaTypesEnum.Video, RtpMediaTypesEnum.Audio };

            lock (_webRtcSessions)
            {
                if (!_webRtcSessions.Any(x => x.Key == webSocketID))
                {
                    var webRtcSession = new WebRtcSession(webSocketID);

                    if (_webRtcSessions.TryAdd(webSocketID, webRtcSession))
                    {
                        webRtcSession.Peer.OnSdpOfferReady += (sdp) => { logger.Debug("Offer SDP: " + sdp); context.WebSocket.Send(sdp); };
                        webRtcSession.Peer.OnDtlsPacket += webRtcSession.DtlsPacketReceived;
                        webRtcSession.Peer.OnMediaPacket += webRtcSession.MediaPacketReceived;
                        webRtcSession.Peer.Initialise(DTLS_CERTIFICATE_THUMBRPINT, null, mediaTypes, IPAddress.Parse(LOCAL_IP_ADDRESS));
                        webRtcSession.Peer.OnClose += () => { PeerClosed(webSocketID); };
                    }
                    else
                    {
                        logger.Error("Failed to add new WebRTC client to sessions dictionary.");
                    }
                }
            }
        }

        private void PeerClosed(string callID)
        {
            try
            {
                logger.Debug("WebRTC session for closed for call ID " + callID + ".");

                WebRtcSession closedSession = null;

                if (!_webRtcSessions.TryRemove(callID, out closedSession))
                {
                    logger.Error("Failed to remove closed WebRTC session from dictionary.");
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception WebRTCDaemon.PeerClosed. " + excp);
            }
        }

        private void WebRtcAnswerReceived(string webSocketID, string sdpAnswer)
        {
            try
            {
                logger.Debug("Answer SDP: " + sdpAnswer);

                var answerSDP = SDP.ParseSDPDescription(sdpAnswer);

                var peer = _webRtcSessions.Where(x => x.Key == webSocketID).Select(x => x.Value.Peer).SingleOrDefault();

                if (peer == null)
                {
                    logger.Warn("No WebRTC client entry exists for web socket ID " + webSocketID + ", ignoring.");
                }
                else
                {
                    logger.Debug("New WebRTC client SDP answer for web socket ID " + webSocketID + ".");

                    peer.SdpSessionID = answerSDP.SessionId;
                    peer.RemoteIceUser = answerSDP.IceUfrag;
                    peer.RemoteIcePassword = answerSDP.IcePwd;

                    foreach (var iceCandidate in answerSDP.IceCandidates)
                    {
                        peer.AppendRemoteIceCandidate(iceCandidate);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SDPExchangeReceiver_SDPAnswerReceived. " + excp.Message);
            }
        }

        private void SendTestPattern()
        {
            try
            {
                unsafe
                {
                    Bitmap testPattern = new Bitmap("wizard.jpeg");
                    //Bitmap testPattern = new Bitmap(@"..\..\max\max257.jpg");

                    SIPSorceryMedia.VPXEncoder vpxEncoder = new VPXEncoder();
                    vpxEncoder.InitEncoder(Convert.ToUInt32(testPattern.Width), Convert.ToUInt32(testPattern.Height), 2160);

                    SIPSorceryMedia.ImageConvert colorConverter = new ImageConvert();

                    byte[] sampleBuffer = null;
                    byte[] encodedBuffer = new byte[5000000];
                    int sampleCount = 0;

                    while (!_exit && sampleCount < 10)
                    {
                        if (_webRtcSessions.Any(x => x.Value.Peer.IsDtlsNegotiationComplete == true && x.Value.Peer.IsClosed == false))
                        {
                            var stampedTestPattern = testPattern.Clone() as System.Drawing.Image;
                            AddTimeStampAndLocation(stampedTestPattern, DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss:fff"), "Test Pattern");
                            sampleBuffer = BitmapToRGB24(stampedTestPattern as System.Drawing.Bitmap);

                            fixed (byte* p = sampleBuffer)
                            {
                                byte[] convertedFrame = null;
                                //colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.RGB24, testPattern.Width, testPattern.Height, VideoSubTypesEnum.I420, ref convertedFrame);
                                colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.BGR24, testPattern.Width, testPattern.Height, VideoSubTypesEnum.I420, ref convertedFrame);

                                fixed (byte* q = convertedFrame)
                                {
                                    int encodeResult = vpxEncoder.Encode(q, convertedFrame.Length, 1, ref encodedBuffer);

                                    if (encodeResult != 0)
                                    {
                                        logger.Warn("VPX encode of video sample failed.");
                                        continue;
                                    }
                                }
                            }

                            stampedTestPattern.Dispose();
                            stampedTestPattern = null;

                            lock (_webRtcSessions)
                            {
                                foreach (var session in _webRtcSessions.Where(x => x.Value.Peer.IsDtlsNegotiationComplete == true && x.Value.Peer.LocalIceCandidates.Any(y => y.RemoteRtpEndPoint != null)))
                                {
                                    session.Value.SendVp8(encodedBuffer);
                                }
                            }

                            encodedBuffer = null;

                            sampleCount++;
                        }

                        Thread.Sleep(30);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SendTestPattern. " + excp);
            }
        }

        private void SendMp4()
        {
            try
            {
                unsafe
                {
                    //Bitmap testPattern = new Bitmap("wizard.jpeg");

                    SIPSorceryMedia.MFVideoSampler sampler = new MFVideoSampler();
                    //videoSampler.Init(videoMode.DeviceIndex, VideoSubTypesEnum.RGB24, videoMode.Width, videoMode.Height);
                    sampler.InitFromFile();

                    uint sampleWidth = 0; // Convert.ToUInt32(sampler.Width);
                    uint sampleHeight = 0; // Convert.ToUInt32(sampler.Height);
                    //VideoSubTypesHelper.GetPixelFormatForVideoSubType(sampleHeight.)

                    logger.Debug($"Sampler width {sampleWidth}, height {sampleHeight}.");

                    SIPSorceryMedia.VPXEncoder vpxEncoder = new VPXEncoder();
                    //vpxEncoder.InitEncoder(sampleWidth, sampleHeight);
                    //vpxEncoder.InitEncoder(Convert.ToUInt32(testPattern.Width), Convert.ToUInt32(testPattern.Height));

                    SIPSorceryMedia.ImageConvert colorConverter = new ImageConvert();

                    byte[] sampleBuffer = null;
                    byte[] encodedBuffer = new byte[4096];
                    int sampleCount = 0;

                    while (!_exit)
                    {
                        if (_webRtcSessions.Any(x => x.Value.Peer.IsDtlsNegotiationComplete == true && x.Value.Peer.IsClosed == false))
                        {
                            var sampleProps = sampler.GetSample(ref sampleBuffer);

                            if (sampleProps.Width != 0 && sampleProps.Height != 0 &&
                                sampleProps.Width != sampleWidth && sampleProps.Height != sampleHeight)
                            {
                                sampleWidth = sampleProps.Width;
                                sampleHeight = sampleProps.Height;

                                vpxEncoder.InitEncoder(sampleWidth, sampleHeight, 2176);

                                Console.WriteLine($"VPX encoder dimensions set to {sampleWidth} x {sampleHeight}.");
                            }

                            // Save frame to Bitmap for diagnostics.
                            //fixed (byte* p = sampleBuffer)
                            //{
                            //    IntPtr ptr = (IntPtr)p;
                            //    Bitmap bmp = new Bitmap((int)sampleWidth, (int)sampleHeight, (int)sampleStride, PixelFormat.Format32bppRgb, ptr);
                            //    bmp.Save(@"bmp\sample_" + sampleCount + ".bmp");
                            //}

                            //var stampedTestPattern = testPattern.Clone() as System.Drawing.Image;
                            //AddTimeStampAndLocation(stampedTestPattern, DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss:fff"), "Test Pattern");
                            //sampleBuffer = BitmapToRGB24(stampedTestPattern as System.Drawing.Bitmap);

                            fixed (byte* p = sampleBuffer)
                            {
                                byte[] convertedFrame = null;
                                //colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.RGB24, testPattern.Width, testPattern.Height, VideoSubTypesEnum.I420, ref convertedFrame);
                                colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.RGB32, (int)sampleWidth, (int)sampleHeight, VideoSubTypesEnum.I420, ref convertedFrame);
                                //colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.RGB32, (int)sampleWidth, (int)sampleHeight, VideoSubTypesEnum.YUY2, ref convertedFrame);

                                fixed (byte* q = convertedFrame)
                                {
                                    //int encodeResult = vpxEncoder.Encode(q, sampleBuffer.Length, 1, ref encodedBuffer);
                                    int encodeResult = vpxEncoder.Encode(q, convertedFrame.Length, 1, ref encodedBuffer);

                                    if (encodeResult != 0)
                                    {
                                        logger.Warn("VPX encode of video sample failed.");
                                        continue;
                                    }
                                }
                            }

                            //stampedTestPattern.Dispose();
                            //stampedTestPattern = null;

                            lock (_webRtcSessions)
                            {
                                foreach (var session in _webRtcSessions.Where(x => x.Value.Peer.IsDtlsNegotiationComplete == true && x.Value.Peer.LocalIceCandidates.Any(y => y.RemoteRtpEndPoint != null)))
                                {
                                    session.Value.SendVp8(encodedBuffer);
                                }
                            }

                            encodedBuffer = null;

                            sampleCount++;
                        }

                        Thread.Sleep(30);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SendTestPattern. " + excp);
            }
        }

        private void SendMp4ViaFile()
        {
            try
            {
                unsafe
                {
                    //Bitmap testPattern = new Bitmap("wizard.jpeg");

                    SIPSorceryMedia.MFVideoSampler sampler = new MFVideoSampler();
                    //videoSampler.Init(videoMode.DeviceIndex, VideoSubTypesEnum.RGB24, videoMode.Width, videoMode.Height);
                    sampler.InitFromFile();

                    uint sampleWidth = 0; // Convert.ToUInt32(sampler.Width);
                    uint sampleHeight = 0; // Convert.ToUInt32(sampler.Height);
                    uint sampleStride = 2176;
                    //VideoSubTypesHelper.GetPixelFormatForVideoSubType(sampleHeight.)

                    logger.Debug($"Sampler width {sampleWidth}, height {sampleHeight}.");

                    SIPSorceryMedia.VPXEncoder vpxEncoder = new VPXEncoder();
                    //vpxEncoder.InitEncoder(sampleWidth, sampleHeight);
                    //vpxEncoder.InitEncoder(Convert.ToUInt32(testPattern.Width), Convert.ToUInt32(testPattern.Height));

                    SIPSorceryMedia.ImageConvert colorConverter = new ImageConvert();

                    byte[] sampleBuffer = null;
                    byte[] encodedBuffer = new byte[1000000];
                    int sampleCount = 0;

                    while (!_exit)
                    {
                        if (_webRtcSessions.Any(x => x.Value.Peer.IsDtlsNegotiationComplete == true && x.Value.Peer.IsClosed == false))
                        {
                            var sampleProps = sampler.GetSample(ref sampleBuffer);

                            if (sampleProps.Width != 0 && sampleProps.Height != 0 &&
                                sampleProps.Width != sampleWidth && sampleProps.Height != sampleHeight)
                            {
                                sampleWidth = sampleProps.Width;
                                sampleHeight = sampleProps.Height;

                                vpxEncoder.InitEncoder(sampleWidth, sampleHeight, sampleStride);

                                Console.WriteLine($"VPX encoder dimensions set to {sampleWidth} x {sampleHeight}.");
                            }

                            // Save frame to Bitmap for diagnostics.
                            if (sampleBuffer.Length > 0)
                            {
                                Bitmap bmp = null;

                                fixed (byte* p = sampleBuffer)
                                {
                                    IntPtr ptr = (IntPtr)p;
                                    bmp = new Bitmap((int)sampleWidth, (int)sampleHeight, (int)sampleStride, PixelFormat.Format32bppRgb, ptr);
                                    //Bitmap bmp = new Bitmap((int)sampleWidth, (int)sampleHeight, (int)sampleStride, PixelFormat.Format24bppRgb, ptr);
                                    //bmp.Save("bmp\\sample_" + sampleCount + ".bmp");
                                    //bmp.Save("bmp\\sample.bmp");
                                }


                                //Bitmap frameBmp = new Bitmap("bmp\\sample_" + sampleCount + ".bmp");
                                var bmpSampleBuffer = BitmapToRGB24(bmp as System.Drawing.Bitmap);
                                //var stampedTestPattern = testPattern.Clone() as System.Drawing.Image;
                                //AddTimeStampAndLocation(stampedTestPattern, DateTime.UtcNow.ToString("dd MMM yyyy HH:mm:ss:fff"), "Test Pattern");
                                //sampleBuffer = BitmapToRGB24(stampedTestPattern as System.Drawing.Bitmap);

                                fixed (byte* p = bmpSampleBuffer)
                                {
                                    byte[] convertedFrame = null;
                                    //colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.RGB24, testPattern.Width, testPattern.Height, VideoSubTypesEnum.I420, ref convertedFrame);
                                    colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.BGR24, (int)sampleWidth, (int)sampleHeight, VideoSubTypesEnum.I420, ref convertedFrame);
                                    //colorConverter.ConvertRGBtoYUV(p, VideoSubTypesEnum.RGB32, (int)sampleWidth, (int)sampleHeight, VideoSubTypesEnum.YUY2, ref convertedFrame);

                                    fixed (byte* q = convertedFrame)
                                    {
                                        //int encodeResult = vpxEncoder.Encode(q, sampleBuffer.Length, 1, ref encodedBuffer);
                                        int encodeResult = vpxEncoder.Encode(q, convertedFrame.Length, 1, ref encodedBuffer);

                                        if (encodeResult != 0)
                                        {
                                            logger.Warn("VPX encode of video sample failed.");
                                            continue;
                                        }
                                    }
                                }

                                //stampedTestPattern.Dispose();
                                //stampedTestPattern = null;

                                lock (_webRtcSessions)
                                {
                                    foreach (var session in _webRtcSessions.Where(x => x.Value.Peer.IsDtlsNegotiationComplete == true
                                        //&& x.Value.Peer.LocalIceCandidates.Any(y => y.MediaType == RtpMediaTypesEnum.Video && y.RemoteRtpEndPoint != null)))
                                        && x.Value.Peer.LocalIceCandidates.Any(y => y.RemoteRtpEndPoint != null)))
                                    {
                                        session.Value.SendVp8(encodedBuffer);
                                    }
                                }

                                encodedBuffer = null;

                                sampleCount++;
                            }
                        }

                        Thread.Sleep(30);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SendTestPattern. " + excp);
            }
        }

        private void SendSineWaveAudio()
        {
            try
            {
                int sampleRate = 8000;
                byte[] buffer = new byte[8000 * 2];
                double amplitude = 0.25 * short.MaxValue;
                double frequency = 1000;

                while (!_exit)
                {
                    for (int n = 0; n < buffer.Length; n += 2)
                    {
                        short sample = (short)(amplitude * Math.Sin((2 * Math.PI * n * frequency) / sampleRate));
                        buffer[n] = (byte)(sample & 0xff);
                        buffer[n + 1] = (byte)(sample >> 8 & 0xff);
                    }

                    lock (_webRtcSessions)
                    {
                        foreach (var session in _webRtcSessions.Where(x => x.Value.Peer.IsDtlsNegotiationComplete == true
                            //&& x.Value.Peer.LocalIceCandidates.Any(y => y.MediaType == RtpMediaTypesEnum.Audio && y.RemoteRtpEndPoint != null)))
                            && x.Value.Peer.LocalIceCandidates.Any(y => y.RemoteRtpEndPoint != null)))
                        {
                            session.Value.SendPcmu(buffer);
                        }
                    }

                    Thread.Sleep(100);
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception PlayPcmAudio. " + excp);
            }
        }

        private void PlayPcmAudio()
        {
            try
            {
                var readerStream = new MediaFoundationReader("max4.mp4");
                WaveFormat waveFormat = new WaveFormat(8000, 16, 2);
                BufferedWaveProvider waveProvider = new BufferedWaveProvider(waveFormat);

                var outputDevice = new WaveOutEvent();
                outputDevice.Init(waveProvider);
                outputDevice.Play();

                unsafe
                {
                    SIPSorceryMedia.MFVideoSampler sampler = new MFVideoSampler();
                    sampler.InitFromFile();

                    while (!_exit)
                    {
                        byte[] decodeBuffer = null;
                        int result = sampler.GetAudioSample(ref decodeBuffer);

                        if (result != 0 || decodeBuffer == null)
                        {
                            logger.Warn($"Failed to get audio sample, error code {result}.");
                        }
                        else
                        {
                            Console.WriteLine($"Decoded sample size " + decodeBuffer.Length + ".");
                            waveProvider.AddSamples(decodeBuffer, 0, decodeBuffer.Length);
                            Thread.Sleep(80);
                        }
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception PlayPcmAudio. " + excp);
            }
        }

        private void SendPcmAudio()
        {
            try
            {
                WaveFormat waveFormat = new WaveFormat(8000, 16, 2);
                //WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(8000, 2);
                BufferedWaveProvider waveProvider = new BufferedWaveProvider(waveFormat);
                //WaveFloatTo16Provider waveFloatTo16Provider = new WaveFloatTo16Provider(waveProvider);

                waveProvider.BufferLength = 100000;
                var outputDevice = new WaveOutEvent();
                outputDevice.Init(waveProvider);
                outputDevice.Play();

                unsafe
                {
                    SIPSorceryMedia.MFVideoSampler sampler = new MFVideoSampler();
                    sampler.InitFromFile();

                    byte[] sampleBuffer = null;

                    while (!_exit)
                    {
                        if (_webRtcSessions.Any(x => x.Value.Peer.IsDtlsNegotiationComplete == true && x.Value.Peer.IsClosed == false))
                        {
                            int result = sampler.GetAudioSample(ref sampleBuffer);

                            if (result != 0)
                            {
                                logger.Warn($"Failed to get audio sample, error code {result}.");
                            }
                            else
                            {
                                Console.WriteLine($"Audio sample size {sampleBuffer.Length}.");

                                var waveBuffer = new WaveBuffer(sampleBuffer);
                                //int samplesRequired = 9600 / 4;
                                //int offset = 0;
                                //int samplesRead = Read(waveBuffer.FloatBuffer, offset / 4, samplesRequired);
                                //return samplesRead * 4;

                                int bytesRead = sampleBuffer.Length;
                                int outIndex = 0;
                                for (int n = 0; n < bytesRead; n += 2)
                                {
                                    waveBuffer.FloatBuffer[outIndex++] = BitConverter.ToInt16(sampleBuffer, n) / 32768f;
                                }
                                int samples = bytesRead / 2;

                                waveProvider.AddSamples(waveBuffer.ByteBuffer, 0, samples);

                                lock (_webRtcSessions)
                                {
                                    foreach (var session in _webRtcSessions.Where(x => x.Value.Peer.IsDtlsNegotiationComplete == true &&
                                        x.Value.Peer.LocalIceCandidates.Any(y => y.RemoteRtpEndPoint != null)))
                                    {
                                        session.Value.SendPcmu(sampleBuffer);
                                    }
                                }
                            }
                        }

                        Thread.Sleep(30);
                    }
                }
            }
            catch (Exception excp)
            {
                logger.Error("Exception SendTestPattern. " + excp);
            }
        }

        private static byte[] BitmapToRGB24(Bitmap bitmap)
        {
            try
            {
                BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                var length = bitmapData.Stride * bitmapData.Height;

                byte[] bytes = new byte[length];

                // Copy bitmap to byte[]
                Marshal.Copy(bitmapData.Scan0, bytes, 0, length);
                bitmap.UnlockBits(bitmapData);

                return bytes;
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }

        private static void AddTimeStampAndLocation(System.Drawing.Image image, string timeStamp, string locationText)
        {
            int pixelHeight = (int)(image.Height * TEXT_SIZE_PERCENTAGE);

            Graphics g = Graphics.FromImage(image);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (StringFormat format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Alignment = StringAlignment.Center;

                using (Font f = new Font("Tahoma", pixelHeight, GraphicsUnit.Pixel))
                {
                    using (var gPath = new GraphicsPath())
                    {
                        float emSize = g.DpiY * f.Size / POINTS_PER_INCH;
                        if (locationText != null)
                        {
                            gPath.AddString(locationText, f.FontFamily, (int)FontStyle.Bold, emSize, new Rectangle(0, TEXT_MARGIN_PIXELS, image.Width, pixelHeight), format);
                        }

                        gPath.AddString(timeStamp /* + " -- " + fps.ToString("0.00") + " fps" */, f.FontFamily, (int)FontStyle.Bold, emSize, new Rectangle(0, image.Height - (pixelHeight + TEXT_MARGIN_PIXELS), image.Width, pixelHeight), format);
                        g.FillPath(Brushes.White, gPath);
                        g.DrawPath(new Pen(Brushes.Black, pixelHeight * TEXT_OUTLINE_REL_THICKNESS), gPath);
                    }
                }
            }
        }
    }

    public class SDPExchangeReceiver : WebSocketBehavior
    {
        public static event Action<WebSocketSharp.Net.WebSockets.WebSocketContext, string> WebSocketOpened;
        public static event Action<string, string> SDPAnswerReceived;

        protected override void OnMessage(MessageEventArgs e)
        {
            SDPAnswerReceived(this.ID, e.Data);
        }

        protected override void OnOpen()
        {
            base.OnOpen();
            WebSocketOpened(this.Context, this.ID);
        }
    }

    public class SineWaveProvider32 : WaveProvider32
    {
        int sample;

        public SineWaveProvider32()
        {
            Frequency = 1000;
            Amplitude = 0.25f; // let's not hurt our ears
        }

        public float Frequency { get; set; }
        public float Amplitude { get; set; }

        public override int Read(float[] buffer, int offset, int sampleCount)
        {
            int sampleRate = WaveFormat.SampleRate;
            for (int n = 0; n < sampleCount; n++)
            {
                buffer[n + offset] = (float)(Amplitude * Math.Sin((2 * Math.PI * sample * Frequency) / sampleRate));
                sample++;
                if (sample >= sampleRate) sample = 0;
            }
            return sampleCount;
        }
    }
}
