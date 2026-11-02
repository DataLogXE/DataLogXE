#region License
/******************************************************************************
 * S7CommPlusDriver
 * 
 * Copyright (C) 2023 Thomas Wiens, th.wiens@gmx.de
 *
 * This file is part of S7CommPlusDriver.
 *
 * S7CommPlusDriver is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 *
 * Modified by DataLogXE in 2026 (changes licensed under the same terms).
 /****************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.IO;
using System.Linq;
using S7CommPlusDriver.ClientApi;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Serilog;
using S7CommPlusDriver.Net;

namespace S7CommPlusDriver
{
    public partial class S7CommPlusConnection
    {
        #region Private Members
        private S7Client m_client;
        private MemoryStream m_ReceivedPDU;
        private readonly ILogger m_log;

        private UInt32 m_SessionId;
        private UInt32 m_SessionId2;
        public UInt32 SessionId2
        {
            get { return m_SessionId2; }
            private set { m_SessionId2 = value; }
        }

        private int m_ReadTimeout = 10000;
        private UInt16 m_SequenceNumber = 0;
        private UInt32 m_IntegrityId = 0;
        private UInt32 m_IntegrityId_Set = 0;
        private CommRessources m_CommRessources = new CommRessources();

        private List<DatablockInfo> dbInfoList;
        private List<PObject> typeInfoList = new List<PObject>();

        // Request / response dispatch: exactly one request is in flight. SendS7plusFunctionObject
        // takes the gate and registers the pending request, WaitForNewS7plusReceived waits for the
        // response with the same sequence number and releases the gate. Late or foreign responses
        // are dropped, notifications take their own path (NotificationReceived).
        private readonly SemaphoreSlim m_RequestGate = new SemaphoreSlim(1, 1);
        private PendingRequest m_Pending;
        private volatile bool m_SessionLost;
        private string m_SessionLostReason;

        private sealed class PendingRequest
        {
            public ushort SequenceNumber;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public byte[] Pdu;
            public int Error;
        }
        #endregion

        #region Public Members
        public int m_LastError = 0;

        /// <summary>Raised on the receive thread for every notification PDU ([protoVersion, 0x33, ...]).</summary>
        public event Action<byte[]> NotificationReceived;

        /// <summary>Raised once when the connection is lost (argument: reason). Not raised by Disconnect().</summary>
        public event Action<string> ConnectionLost;

        public bool Connected => m_client != null && m_client.Connected && !m_SessionLost && m_SessionId != 0;

        public string ConnectionLostReason => m_SessionLostReason;

        public CommRessources Ressources => m_CommRessources;

        /// <summary>PAOM string of the session, e.g. "1;6ES7 511-1AK02-0AB0 ;V2.9".</summary>
        public string SessionPaom { get; private set; }

        /// <summary>Effective protection level read during the legitimation (1 = full access ... 4 = no access).</summary>
        public uint EffectiveProtectionLevel { get; private set; }

        public int ReadTimeoutMs => m_ReadTimeout;
        #endregion

        public S7CommPlusConnection(ILogger log = null)
        {
            m_log = log ?? Log.Logger;
        }

        #region Private Methods

        private UInt16 GetNextSequenceNumber()
        {
            if (m_SequenceNumber == UInt16.MaxValue)
            {
                m_SequenceNumber = 1;
            }
            else
            {
                m_SequenceNumber++;
            }
            return m_SequenceNumber;
        }

        // We must count the IntegrityId for different functions of the protocol.
        // As a first guess functions for setting variables need separate counters.
        // Use the functioncode to differ between the which sequence/integrity counter values.
        private UInt32 GetNextIntegrityId(ushort functioncode)
        {
            UInt32 ret;
            switch (functioncode)
            {
                case Functioncode.SetMultiVariables:
                case Functioncode.SetVariable:
                case Functioncode.SetVarSubStreamed:
                case Functioncode.DeleteObject:
                case Functioncode.CreateObject:
                    if (m_IntegrityId_Set == UInt32.MaxValue)
                    {
                        m_IntegrityId_Set = 0;
                    }
                    else
                    {
                        m_IntegrityId_Set++;
                    }
                    ret = m_IntegrityId_Set;
                    break;
                default:
                    if (m_IntegrityId == UInt32.MaxValue)
                    {
                        m_IntegrityId = 0;
                    }
                    else
                    {
                        m_IntegrityId++;
                    }
                    ret = m_IntegrityId;
                    break;
            }
            return ret;
        }

        /// <summary>Waits for the response of the request sent last by SendS7plusFunctionObject.</summary>
        private void WaitForNewS7plusReceived(int Timeout)
        {
            var pending = m_Pending;
            if (pending == null)
            {
                // Send failed before, the error is already in m_LastError.
                if (m_LastError == 0) m_LastError = S7Consts.errIsoInvalidPDU;
                return;
            }
            try
            {
                if (!pending.Done.Wait(Timeout))
                {
                    m_log.Warning("S7CommPlusConnection: no response for sequence number {Seq} within {Timeout} ms, closing the connection", pending.SequenceNumber, Timeout);
                    m_LastError = S7Consts.errTCPDataReceive;
                    // The sequence is out of step now; a clean restart is the only safe way on.
                    LoseSession("response timeout");
                    return;
                }
                if (pending.Error != 0)
                {
                    m_LastError = pending.Error;
                    return;
                }
                m_ReceivedPDU = new MemoryStream(pending.Pdu, false);
            }
            finally
            {
                m_Pending = null;
                m_RequestGate.Release();
            }
        }

        private int SendS7plusFunctionObject(IS7pRequest funcObj)
        {
            if (m_client == null || !m_client.Connected || m_SessionLost)
            {
                m_LastError = S7Consts.errTCPNotConnected;
                return m_LastError;
            }
            if (!m_RequestGate.Wait(m_ReadTimeout * 2))
            {
                m_log.Error("S7CommPlusConnection: request gate not released, previous request still pending");
                m_LastError = S7Consts.errCliJobPending;
                return m_LastError;
            }

            // If we don't have a SessionId, this must be the first CreateObjectRequest, where we use the Id for NullServerSession
            if (m_SessionId == 0)
            {
                funcObj.SessionId = Ids.ObjectNullServerSession;
            }
            else
            {
                funcObj.SessionId = m_SessionId;
            }

            // Insert SequenceNumber and IntegrityId, if neccessary for object type and state of communication
            funcObj.SequenceNumber = GetNextSequenceNumber();
            if (funcObj.WithIntegrityId)
            {
                funcObj.IntegrityId = GetNextIntegrityId(funcObj.FunctionCode);
            }

            var pending = new PendingRequest { SequenceNumber = funcObj.SequenceNumber };
            m_Pending = pending;

            MemoryStream stream = new MemoryStream();
            funcObj.Serialize(stream);
            int res = SendS7plusPDUdata(stream.GetBuffer(), (int)stream.Length, funcObj.ProtocolVersion);
            if (res != 0)
            {
                m_Pending = null;
                m_RequestGate.Release();
                m_LastError = res;
            }
            return res;
        }

        private int SendS7plusPDUdata(byte[] sendPduData, int bytesToSend, byte protoVersion)
        {
            int curSize;
            int sourcePos = 0;
            int sendLen;
            int NegotiatedIsoPduSize = m_client.TpduSize;

            // 4 Byte TPKT Header
            // 3 Byte ISO-Header
            // 5 Byte TLS Header + 17 Bytes addition from TLS
            // 4 Byte S7CommPlus Header
            // 4 Byte S7CommPlus Trailer (must fit into last PDU)
            int MaxSize = NegotiatedIsoPduSize - 4 - 3 - 5 - 17 - 4 - 4;
            byte[] packet = new byte[MaxSize + 8];

            while (bytesToSend > 0)
            {
                if (bytesToSend > MaxSize)
                {
                    curSize = MaxSize;
                    bytesToSend -= MaxSize;
                }
                else
                {
                    curSize = bytesToSend;
                    bytesToSend -= curSize;
                }
                // Header
                packet[0] = 0x72;
                packet[1] = protoVersion;
                packet[2] = (byte)(curSize >> 8);
                packet[3] = (byte)(curSize & 0x00FF);
                // Data part
                Array.Copy(sendPduData, sourcePos, packet, 4, curSize);
                sourcePos += curSize;
                sendLen = 4 + curSize;

                // Trailer only in last packet
                if (bytesToSend == 0)
                {
                    packet[sendLen++] = 0x72;
                    packet[sendLen++] = protoVersion;
                    packet[sendLen++] = 0;
                    packet[sendLen++] = 0;
                }
                int res = m_client.Send(packet, sendLen);
                if (res != 0)
                {
                    return res;
                }
            }
            return 0;
        }

        // Receive thread: one complete PDU [protoVersion, opcode, ...].
        private void OnPduReceived(byte[] pdu)
        {
            if (pdu.Length < 2)
            {
                return;
            }
            byte opcode = pdu[1];
            if (opcode == Opcode.Notification)
            {
                NotificationReceived?.Invoke(pdu);
                return;
            }
            if (opcode != Opcode.Response && opcode != Opcode.Response2)
            {
                m_log.Debug("S7CommPlusConnection: ignoring PDU with opcode 0x{Opcode:X2}", opcode);
                return;
            }
            // ver(1) opcode(1) reserved(2) functioncode(2) reserved(2) sequencenumber(2)
            if (pdu.Length < 10)
            {
                return;
            }
            ushort seq = (ushort)((pdu[8] << 8) | pdu[9]);
            var pending = m_Pending;
            if (pending == null || pending.SequenceNumber != seq || pending.Done.IsSet)
            {
                m_log.Warning("S7CommPlusConnection: dropping response with unexpected sequence number {Seq}", seq);
                return;
            }
            pending.Pdu = pdu;
            pending.Done.Set();
        }

        private void OnSystemEventReceived(byte[] evt)
        {
            SystemEvent sysevt;
            try
            {
                sysevt = SystemEvent.DeserializeFromPdu(new MemoryStream(evt, false));
            }
            catch (Exception ex)
            {
                m_log.Warning("S7CommPlusConnection: undecodable system event: {Error}", ex.Message);
                return;
            }
            if (sysevt != null && sysevt.IsFatalError())
            {
                m_log.Warning("S7CommPlusConnection: fatal system event from the PLC: {Event}", sysevt.ToString());
                LoseSession("fatal system event from the PLC");
            }
        }

        private void OnClientClosed(string reason)
        {
            var pending = m_Pending;
            if (pending != null && !pending.Done.IsSet)
            {
                pending.Error = S7Consts.errCliSessionLost;
                pending.Done.Set();
            }
            if (!m_SessionLost)
            {
                m_SessionLost = true;
                m_SessionLostReason ??= reason;
                if (m_SessionId != 0)
                {
                    try { ConnectionLost?.Invoke(reason); }
                    catch (Exception ex) { m_log.Error(ex, "S7CommPlusConnection: ConnectionLost handler failed"); }
                }
            }
        }

        private void LoseSession(string reason)
        {
            m_SessionLostReason ??= reason;
            m_client?.Disconnect();
        }

        private void DisconnectClient()
        {
            m_client?.Disconnect();
        }

        private int checkResponseWithIntegrity(IS7pRequest request, IS7pResponse response)
        {
            if (response == null)
            {
                m_log.Error("checkResponseWithIntegrity: ERROR! response == null");
                return S7Consts.errIsoInvalidPDU;
            }
            if (request.SequenceNumber != response.SequenceNumber)
            {
                m_log.Error("checkResponseWithIntegrity: ERROR! SeqenceNumber of Response ({ResponseSeq}) doesn't match Request ({RequestSeq})", response.SequenceNumber, request.SequenceNumber);
                return S7Consts.errIsoInvalidPDU;
            }
            // Overflow is possible and allowed
            UInt32 reqIntegCheck = (UInt32)request.SequenceNumber + request.IntegrityId;
            if (response.IntegrityId != reqIntegCheck)
            {
                m_log.Warning("checkResponseWithIntegrity: IntegrityId of the Response ({ResponseIntegrityId}) doesn't match Request ({ReqIntegCheck})", response.IntegrityId, reqIntegCheck);
                // Don't return this as error so far
            }
            return 0;
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Establishes a connection to the PLC.
        /// </summary>
        /// <param name="address">PLC IP address or host name</param>
        /// <param name="password">PLC password (if set)</param>
        /// <param name="username">PLC user (new user management, leave empty for the legacy password)</param>
        /// <param name="timeoutMs">connect and response timeout in milliseconds</param>
        /// <param name="port">TCP port (default 102)</param>
        /// <returns>0 or an S7Consts error code</returns>
        public int Connect(string address, string password = "", string username = "", int timeoutMs = 10000, int port = S7Client.DefaultPort)
        {
            if (m_client != null)
            {
                throw new InvalidOperationException("S7CommPlusConnection can only be connected once; create a new instance to reconnect.");
            }
            if (timeoutMs > 0) {
                m_ReadTimeout = timeoutMs;
            }
            password ??= "";
            username ??= "";

            m_LastError = 0;
            int res;
            int Elapsed = Environment.TickCount;
            m_client = new S7Client(m_log);
            m_client.PduReceived = OnPduReceived;
            m_client.SystemEventReceived = OnSystemEventReceived;
            m_client.Closed = OnClientClosed;

            res = m_client.Connect(address, port, Encoding.ASCII.GetBytes("SIMATIC-ROOT-HMI"), m_ReadTimeout);
            if (res != 0)
                return res;

            #region Step 1: Unencrypted InitSSL Request / Response

            InitSslRequest sslReq = new InitSslRequest(ProtocolVersion.V1, 0, 0);
            res = SendS7plusFunctionObject(sslReq);
            if (res != 0)
            {
                DisconnectClient();
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                DisconnectClient();
                return m_LastError;
            }
            InitSslResponse sslRes;
            sslRes = InitSslResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (sslRes == null)
            {
                m_log.Error("S7CommPlusConnection - Connect: InitSslResponse with Error!");
                DisconnectClient();
                return S7Consts.errIsoInvalidPDU;
            }

            #endregion

            #region Step 2: Activate TLS. Everything from here onwards is TLS encrypted.

            res = m_client.SslActivate(m_ReadTimeout);
            if (res != 0)
            {
                DisconnectClient();
                return res;
            }

            #endregion

            #region Step 3: CreateObjectRequest / Response (with TLS)

            var createObjReq = new CreateObjectRequest(ProtocolVersion.V1, 0, false);
            createObjReq.SetNullServerSessionData();
            res = SendS7plusFunctionObject(createObjReq);
            if (res != 0)
            {
                DisconnectClient();
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                DisconnectClient();
                return m_LastError;
            }

            var createObjRes = CreateObjectResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (createObjRes == null || createObjRes.ObjectIds == null || createObjRes.ObjectIds.Count < 2)
            {
                m_log.Error("S7CommPlusConnection - Connect: CreateObjectResponse with Error!");
                DisconnectClient();
                return S7Consts.errIsoInvalidPDU;
            }
            // There are (always?) at least two IDs in the response.
            // Usually the first is used for polling data, and the 2nd for jobs which use notifications, e.g. alarming, subscriptions.
            m_SessionId = createObjRes.ObjectIds[0];
            m_SessionId2 = createObjRes.ObjectIds[1];
            m_log.Debug("S7CommPlusConnection - Connect: Using SessionId=0x{SessionId:X04}", m_SessionId);

            // Evaluate Struct 314
            PValue sval = createObjRes.ResponseObject.GetAttribute(Ids.ServerSessionVersion);
            ValueStruct serverSession = sval as ValueStruct;
            if (serverSession == null)
            {
                m_log.Error("S7CommPlusConnection - Connect: ServerSessionVersion missing in the CreateObjectResponse");
                DisconnectClient();
                return S7Consts.errIsoInvalidPDU;
            }
            SessionPaom = (serverSession.GetStructElement((uint)Ids.LID_SessionVersionSystemPAOMString) as ValueWString)?.GetValue();

            #endregion

            #region Step 4: SetMultiVariablesRequest / Response

            var setMultiVarReq = new SetMultiVariablesRequest(ProtocolVersion.V2);
            setMultiVarReq.SetSessionSetupData(m_SessionId, serverSession);
            res = SendS7plusFunctionObject(setMultiVarReq);
            if (res != 0)
            {
                DisconnectClient();
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                DisconnectClient();
                return m_LastError;
            }

            var setMultiVarRes = SetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (setMultiVarRes == null)
            {
                m_log.Error("S7CommPlusConnection - Connect: SetMultiVariablesResponse with Error!");
                DisconnectClient();
                return S7Consts.errIsoInvalidPDU;
            }

            #endregion

            #region Step 5: Read SystemLimits
            res = m_CommRessources.ReadMax(this);
            if (res != 0)
            {
                DisconnectClient();
                return res;
            }
            #endregion

            #region Step 6: Password
            res = legitimate(serverSession, password, username);
            if (res != 0) {
                DisconnectClient();
                return res;
            }
            #endregion

            // If everything has been error-free up to this point, then the connection has been established successfully.
            m_log.Debug("S7CommPlusConnection - Connect: Time for connection establishment: {ElapsedMs} ms.", Environment.TickCount - Elapsed);
            return 0;
        }

        /// <summary>Deletes the session (best effort, short timeout) and closes the socket.</summary>
        public void Disconnect()
        {
            if (m_client == null)
            {
                return;
            }
            if (Connected)
            {
                int saved = m_ReadTimeout;
                m_ReadTimeout = Math.Min(saved, 2000);
                try
                {
                    DeleteObject(m_SessionId);
                }
                catch (Exception ex)
                {
                    m_log.Debug("S7CommPlusConnection - Disconnect: DeleteObject failed: {Error}", ex.Message);
                }
                m_ReadTimeout = saved;
            }
            m_SessionId = 0;
            m_SessionId2 = 0;
            m_client.Disconnect();
        }

        /// <summary>Liveness check: reads one system limit. 0 when the PLC answered.</summary>
        public int Ping()
        {
            var adr = new ItemAddress { AccessArea = Ids.ObjectRoot, AccessSubArea = Ids.SystemLimits };
            adr.LID.Add(0);
            int res = ReadValues(new List<ItemAddress> { adr }, out _, out _);
            return res;
        }

        /// <summary>
        /// Deletes the object with the given Id.
        /// </summary>
        /// <param name="deleteObjectId">The object Id to delete</param>
        /// <returns>0 on success</returns>
        private int DeleteObject(uint deleteObjectId)
        {
            int res;
            var delObjReq = new DeleteObjectRequest(ProtocolVersion.V2);
            delObjReq.DeleteObjectId = deleteObjectId;
            res = SendS7plusFunctionObject(delObjReq);
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }
            // If we delete our own session id, then there's no IntegrityId in the response.
            // And the error code gives an error, but not a fatal one.
            // If we delete another object, there should be an IntegrityId in the response, and
            // the response gives no error.
            if (deleteObjectId == m_SessionId)
            {
                var delObjRes = DeleteObjectResponse.DeserializeFromPdu(m_ReceivedPDU, false);
                m_log.Debug("S7CommPlusConnection - DeleteSession: Deleted our own Session Id object, not checking the response.");
                m_SessionId = 0; // not valid anymore
                m_SessionId2 = 0;
            }
            else
            {
                var delObjRes = DeleteObjectResponse.DeserializeFromPdu(m_ReceivedPDU, true);
                res = checkResponseWithIntegrity(delObjReq, delObjRes);
                if (res != 0)
                {
                    return res;
                }
                if (delObjRes.ReturnValue != 0)
                {
                    m_log.Error("S7CommPlusConnection - DeleteSession: Executed with Error! ReturnValue={ReturnValue}", delObjRes.ReturnValue);
                    res = -1;
                }
            }
            return res;
        }

        public int ReadValues(List<ItemAddress> addresslist, out List<object> values, out List<UInt64> errors)
        {
            // The requester must pass the internal type with the request, otherwise not all return values can be converted automatically.
            // For example, strings are transmitted as UInt-Array.
            values = new List<object>();
            errors = new List<UInt64>();
            // Initialize error fields to error value
            for (int i = 0; i < addresslist.Count; i++)
            {
                values.Add(null);
                errors.Add(0xffffffffffffffff);
            }

            // Split request into chunks, taking the MaxTags per request into account
            int chunk_startIndex = 0;
            int count_perChunk = 0;
            do
            {
                int res;
                var getMultiVarReq = new GetMultiVariablesRequest(ProtocolVersion.V2);

                getMultiVarReq.AddressList.Clear();
                count_perChunk = 0;
                while (count_perChunk < m_CommRessources.TagsPerReadRequestMax  && (chunk_startIndex + count_perChunk) < addresslist.Count)
                {
                    getMultiVarReq.AddressList.Add(addresslist[chunk_startIndex + count_perChunk]);
                    count_perChunk++;
                }

                res = SendS7plusFunctionObject(getMultiVarReq);
                m_LastError = 0;
                WaitForNewS7plusReceived(m_ReadTimeout);
                if (m_LastError != 0)
                {
                    return m_LastError;
                }

                var getMultiVarRes = GetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
                res = checkResponseWithIntegrity(getMultiVarReq, getMultiVarRes);
                if (res != 0)
                {
                    return res;
                }
                // ReturnValue shows also an error, if only one single variable could not be read
                if (getMultiVarRes.ReturnValue != 0)
                {
                    m_log.Error("S7CommPlusConnection - ReadValues: Executed with Error! ReturnValue={ReturnValue}", getMultiVarRes.ReturnValue);
                }

                // TODO: If a variable could not be read, there is no value, but there is an ErrorValue.
                // The user must therefore check whether Value != null. Maybe there's a more elegant solution.
                foreach (var v in getMultiVarRes.Values)
                {
                    values[chunk_startIndex + (int)v.Key - 1] = v.Value;
                    // Initialize error to 0, will be overwritten below if there was an error on an item.
                    errors[chunk_startIndex + (int)v.Key - 1] = 0;
                }

                foreach (var ev in getMultiVarRes.ErrorValues)
                {
                    errors[chunk_startIndex + (int)ev.Key - 1] = ev.Value;
                }
                chunk_startIndex += count_perChunk;

            } while (chunk_startIndex < addresslist.Count);

            return m_LastError;
        }

        public int WriteValues(List<ItemAddress> addresslist, List<PValue> values, out List<UInt64> errors)
        {
            int res;
            errors = new List<UInt64>();
            for (int i = 0; i < addresslist.Count; i++)
            {
                // Initialize to no error value, as there's no explicit value for write success.
                errors.Add(0);
            }

            // Split request into chunks, taking the MaxTags per request into account
            int chunk_startIndex = 0;
            int count_perChunk = 0;
            do
            {
                var setMultiVarReq = new SetMultiVariablesRequest(ProtocolVersion.V2);
                setMultiVarReq.AddressListVar.Clear();
                setMultiVarReq.ValueList.Clear();
                count_perChunk = 0;
                while (count_perChunk < m_CommRessources.TagsPerWriteRequestMax && (chunk_startIndex + count_perChunk) < addresslist.Count)
                {
                    setMultiVarReq.AddressListVar.Add(addresslist[chunk_startIndex + count_perChunk]);
                    setMultiVarReq.ValueList.Add(values[chunk_startIndex + count_perChunk]);
                    count_perChunk++;
                }

                res = SendS7plusFunctionObject(setMultiVarReq);
                if (res != 0)
                {
                    return res;
                }
                m_LastError = 0;
                WaitForNewS7plusReceived(m_ReadTimeout);
                if (m_LastError != 0)
                {
                    return m_LastError;
                }

                var setMultiVarRes = SetMultiVariablesResponse.DeserializeFromPdu(m_ReceivedPDU);
                res = checkResponseWithIntegrity(setMultiVarReq, setMultiVarRes);
                if (res != 0)
                {
                    return res;
                }
                // ReturnValue shows also an error, if only one single variable could not be written
                if (setMultiVarRes.ReturnValue != 0)
                {
                    m_log.Error("S7CommPlusConnection - WriteValues: Write with errors. ReturnValue={ReturnValue}", setMultiVarRes.ReturnValue);
                }

                foreach (var ev in setMultiVarRes.ErrorValues)
                {
                    errors[chunk_startIndex + (int)ev.Key - 1] = ev.Value;
                }
                chunk_startIndex += count_perChunk;

            } while (chunk_startIndex < addresslist.Count);

            return m_LastError;
        }

        public int SetPlcOperatingState(Int32 state)
        {
            int res;
            var setVarReq = new SetVariableRequest(ProtocolVersion.V2);
            setVarReq.InObjectId = Ids.NativeObjects_theCPUexecUnit_Rid;
            setVarReq.Address = Ids.CPUexecUnit_operatingStateReq;
            setVarReq.Value = new ValueDInt(state);

            res = SendS7plusFunctionObject(setVarReq);
            if (res != 0)
            {
                DisconnectClient();
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                DisconnectClient();
                return m_LastError;
            }

            var setVarRes = SetVariableResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (setVarRes == null)
            {
                m_log.Error("S7CommPlusConnection - Connect: SetVariableResponse with Error!");
                DisconnectClient();
                return S7Consts.errIsoInvalidPDU;
            }

            return 0;
        }

        public int Browse(out List<VarInfo> varInfoList)
        {
            int res;
            varInfoList = new List<VarInfo>();
            Browser vars = new Browser();
            ExploreRequest exploreReq;
            ExploreResponse exploreRes;

            #region Read all objects

            var exploreData = new List<BrowseData>();

            exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = Ids.NativeObjects_thePLCProgram_Rid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            // We want to know the following attributes
            exploreReq.AddressList.Add(Ids.ObjectVariableTypeName);
            exploreReq.AddressList.Add(Ids.Block_BlockNumber);
            exploreReq.AddressList.Add(Ids.ASObjectES_Comment);

            res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }

            exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            #endregion

            #region Evaluate all data blocks that then need to be browsed

            var obj = exploreRes.Objects.FirstOrDefault(o => o.ClassId == Ids.PLCProgram_Class_Rid);
            if (obj == null)
            {
                m_log.Warning("Browse: Explore response does not contain PLCProgram object (ClassId={ClassId}). " +
                    "Access credentials are incorrect.", Ids.PLCProgram_Class_Rid);
                m_LastError = S7Consts.errCliAccessDenied;
                return m_LastError;
            }

            foreach (var ob in obj.GetObjects())
            {
                switch (ob.ClassId)
                {
                    case Ids.DB_Class_Rid:
                        UInt32 relid = ob.RelationId;
                        UInt32 area = (relid >> 16);
                        UInt32 num = relid & 0xffff;
                        if (area == 0x8a0e)
                        {
                            var name = (ValueWString)(ob.GetAttribute(Ids.ObjectVariableTypeName));
                            BrowseData data = new BrowseData();
                            data.db_block_relid = relid;
                            data.db_name = name.GetValue();
                            data.db_number = num;
                            exploreData.Add(data);
                        }
                        break;
                }
            }

            #endregion

            #region Determine the TypeInfo RID to the RelId from the first response
            // By querying LID = 1 from all DBs you get the RID back with which the type information can be queried.
            // This is neccessary because, for example, with instance DBs (e.g. TON), the type information must
            // not be accessed via the RID of the DB, but of the RID of the TON.
            var readlist = new List<ItemAddress>();
            var values = new List<object>();
            var errors = new List<UInt64>();

            foreach (var data in exploreData)
            {
                if (data.db_number > 0) // only process datablocks here, no marker, timer etc.
                {
                    // Insert the variable address
                    var adr1 = new ItemAddress();
                    adr1.AccessArea = data.db_block_relid;
                    adr1.AccessSubArea = Ids.DB_ValueActual;
                    adr1.LID.Add(1);
                    readlist.Add(adr1);
                }
            }
            res = ReadValues(readlist, out values, out errors);
            if (res != 0)
            {
                return res;
            }
            #endregion

            #region Pass the preliminary information for recombination to ExploreSymbols

            // Add the response information to the list
            for (int i = 0; i < values.Count; i++)
            {
                if (errors[i] == 0)
                {
                    ValueRID rid = (ValueRID)values[i];
                    var data = exploreData[i];
                    data.db_block_ti_relid = rid.GetValue();
                    exploreData[i] = data;
                }
                else
                {
                    // On error, set the relid to zero, will be removed from the list in the next step.
                    // TODO: Report this as an error?
                    var data = exploreData[i];
                    data.db_block_ti_relid = 0;
                    exploreData[i] = data;
                }
            }
            // Remove elements with db_block_ti_relid == 0. This occurs e.g. on datablocks only present in load memory.
            // The informations can't be used any further (at least not for variable access).
            exploreData.RemoveAll(item => item.db_block_ti_relid == 0);

            foreach (var ed in exploreData)
            {
                vars.AddBlockNode(eNodeType.Root, ed.db_name, ed.db_block_relid, ed.db_block_ti_relid);
            }

            // Add IQMCT areas manually
            vars.AddBlockNode(eNodeType.Root, "IArea", Ids.NativeObjects_theIArea_Rid, 0x90010000);
            vars.AddBlockNode(eNodeType.Root, "QArea", Ids.NativeObjects_theQArea_Rid, 0x90020000);
            vars.AddBlockNode(eNodeType.Root, "MArea", Ids.NativeObjects_theMArea_Rid, 0x90030000);
            vars.AddBlockNode(eNodeType.Root, "S7Timers", Ids.NativeObjects_theS7Timers_Rid, 0x90050000);
            vars.AddBlockNode(eNodeType.Root, "S7Counters", Ids.NativeObjects_theS7Counters_Rid, 0x90060000);

            #endregion

            #region Read the Type Info Container (as a single big PDU, must be proven to be the way to go in big programs)
            exploreReq = new ExploreRequest(ProtocolVersion.V2);
            // With ObjectOMSTypeInfoContainer we get all in a big PDU (with maybe hundreds of fragments)
            exploreReq.ExploreId = Ids.ObjectOMSTypeInfoContainer;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }
            #endregion

            #region Process the response, and build the complete variables list
            exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }
            var objs = exploreRes.Objects.First(o => o.ClassId == Ids.ClassOMSTypeInfoContainer);

            vars.SetTypeInfoContainerObjects(objs.GetObjects());
            vars.BuildTree();
            vars.BuildFlatList();
            varInfoList = vars.GetVarInfoList();
            #endregion

            return 0;
        }

        /// <summary>
        /// Gets the first level of a tag symbol string. Removes the " used to escape special chars.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <returns>The first level of the symbol string</returns>
        /// <exception cref="Exception">Symbol syntax error</exception>
        private string parseSymbolLevel(ref string symbol)
        {
            if (symbol.StartsWith("\""))
            {
                int idx = symbol.IndexOf('"', 1);
                if (idx < 0) throw new Exception("Symbol syntax error");
                string lvl = symbol.Substring(1, idx - 1);
                symbol = symbol.Remove(0, idx + 1);
                if (symbol.StartsWith(".")) symbol = symbol.Remove(0, 1);
                return lvl;
            }
            else
            {
                int idx = symbol.IndexOf('.');
                int idx2 = symbol.IndexOf('[', 1);
                if (idx2 >= 0 && (idx2 < idx || idx < 0)) idx = idx2;
                if (idx >= 0)
                {
                    string lvl = symbol.Substring(0, idx);
                    symbol = symbol.Remove(0, idx);
                    if (symbol.StartsWith(".")) symbol = symbol.Remove(0, 1);
                    return lvl;
                }
                else
                {
                    string lvl = symbol;
                    symbol = "";
                    return lvl;
                }
            }
        }

        /// <summary>
        /// Gets the typeinfo by given ti relid from the internal buffer. If it's not found in the buffer
        /// it's fetched from the PLC and stored in the buffer.
        /// </summary>
        /// <param name="ti_relid">type info relid</param>
        /// <returns>type info</returns>
        /// <exception cref="Exception">Could not get type info</exception>
        public PObject getTypeInfoByRelId(uint ti_relid)
        {
            PObject pObj = typeInfoList.Find(ti => ti.RelationId == ti_relid);
            if (pObj == null)
            {
                // Type info not found in list, request it from plc
                List<PObject> newPObj = new List<PObject>();
                if (GetTypeInformation(ti_relid, out newPObj) != 0) throw new Exception("Could not get type info");
                typeInfoList.AddRange(newPObj);
                // Try again
                pObj = typeInfoList.Find(ti => ti.RelationId == ti_relid);
            }
            return pObj;
        }

        /// <summary>
        /// Calculates the access sequence for 1 dimensional arrays.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <param name="varType">Var type that holds the dim info</param>
        /// <param name="varInfo">used to build access sequence</param>
        /// <exception cref="Exception">Symbol syntax error</exception>
        private void calcAccessSeqFor1DimArray(ref string symbol, PVartypeListElement varType, VarInfo varInfo)
        {
            Regex re = new Regex(@"^\[(-?\d+)\]");
            Match m = re.Match(symbol);
            if (!m.Success) throw new Exception("Symbol syntax error");
            parseSymbolLevel(ref symbol); // remove index from symbol string
            int arrayIndex = int.Parse(m.Groups[1].Value);

            var ioit = (IOffsetInfoType_1Dim)varType.OffsetInfoType;
            uint arrayElementCount = ioit.GetArrayElementCount();
            int arrayLowerBounds = ioit.GetArrayLowerBounds();

            if (arrayIndex - arrayLowerBounds > arrayElementCount) throw new Exception("Out of bounds");
            if (arrayIndex < arrayLowerBounds) throw new Exception("Out of bounds");
            varInfo.AccessSequence += "." + String.Format("{0:X}", arrayIndex - arrayLowerBounds);
            if (varType.OffsetInfoType.HasRelation()) varInfo.AccessSequence += ".1"; // additional ".1" for array of struct
        }

        /// <summary>
        /// Calculates the access sequence for multi-dimensional arrays.
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <param name="varType">Var type that holds the dim info</param>
        /// <param name="varInfo">used to build access sequence</param>
        /// <exception cref="Exception">Symbol syntax error</exception>
        private void calcAccessSeqForMDimArray(ref string symbol, PVartypeListElement varType, VarInfo varInfo)
        {
            Regex re = new Regex(@"^\[( ?-?\d+ ?(, ?-?\d+ ?)+)\]");
            Match m = re.Match(symbol);
            if (!m.Success) throw new Exception("Symbol syntax error");
            parseSymbolLevel(ref symbol); // remove index from symbol string
            string idxs = m.Groups[1].Value.Replace(" ", "");

            int[] indexes = Array.ConvertAll(idxs.Split(','), e => int.Parse(e));
            var ioit = (IOffsetInfoType_MDim)varType.OffsetInfoType;
            uint[] MdimArrayElementCount = (uint[])ioit.GetMdimArrayElementCount().Clone();
            int[] MdimArrayLowerBounds = ioit.GetMdimArrayLowerBounds();

            // check dim count
            int dimCount = MdimArrayElementCount.Aggregate(0, (acc, act) => acc += (act > 0) ? 1 : 0);
            if (dimCount != indexes.Count()) throw new Exception("Out of bounds");
            // check bounds
            for (int i = 0; i < dimCount; ++i)
            {
                indexes[i] = (indexes[i] - MdimArrayLowerBounds[dimCount - i - 1]);
                if (indexes[i] > MdimArrayElementCount[dimCount - i - 1]) throw new Exception("Out of bounds");
                if (indexes[i] < 0) throw new Exception("Out of bounds");
            }

            // calc dim size
            if (varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL)
            {
                MdimArrayElementCount[0] += 8 - MdimArrayElementCount[0] % 8; // for bool must be a mutiple of 8!
            }
            uint[] dimSize = new uint[dimCount];
            uint g = 1;
            for (int i = 0; i < dimCount - 1; ++i)
            {
                dimSize[i] = g;
                g *= MdimArrayElementCount[i];
            }
            dimSize[dimCount - 1] = g;

            // calc id
            int arrayIndex = 0;
            for (int i = 0; i < dimCount; ++i)
            {
                arrayIndex += indexes[i] * (int)dimSize[dimCount - i - 1];
            }

            varInfo.AccessSequence += "." + String.Format("{0:X}", arrayIndex);
            if (varType.OffsetInfoType.HasRelation()) varInfo.AccessSequence += ".1"; // additional ".1" for array of struct
        }

        /// <summary>
        /// Browses the symbol level by level recursively. Fetches missing type info automatically from the plc.
        /// </summary>
        /// <param name="ti_relid">type info relid</param>
        /// <param name="symbol">plc tag symbol</param>
        /// <param name="varInfo">used to build access sequence</param>
        /// <returns>plc tag or null if not found</returns>
        /// <exception cref="Exception">Symbol syntax error, Out of bounds</exception>
        private PlcTag browsePlcTagBySymbol(uint ti_relid, ref string symbol, VarInfo varInfo)
        {
            PObject pObj = getTypeInfoByRelId(ti_relid);
            if (pObj == null) throw new Exception("Could not get type info");
            string levelName = parseSymbolLevel(ref symbol);
            // find level name of symbol in var list
            int idx = pObj.VarnameList?.Names?.IndexOf(levelName) ?? -1;
            if (idx < 0) return null;
            PVartypeListElement varType = pObj.VartypeList.Elements[idx];
            varInfo.AccessSequence += "." + String.Format("{0:X}", varType.LID);
            bool is1Dim = false;
            if (varType.OffsetInfoType.Is1Dim())
            {
                if (symbol == "")
                {
                    is1Dim = true;
                }
                else
                {
                    calcAccessSeqFor1DimArray(ref symbol, varType, varInfo);
                }
            }
            if (varType.OffsetInfoType.IsMDim())
            {
                calcAccessSeqForMDimArray(ref symbol, varType, varInfo);
            }
            if (varType.OffsetInfoType.HasRelation())
            {
                if (symbol.Length <= 0 && varType.Softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DTL)
                {
                    return PlcTags.TagFactory(varInfo.Name, new ItemAddress(varInfo.AccessSequence), varType.Softdatatype, is1Dim);
                }
                if (symbol.Length <= 0)
                {
                    return null;
                }
                else
                {
                    var ioit = (IOffsetInfoType_Relation)varType.OffsetInfoType;
                    return browsePlcTagBySymbol(ioit.GetRelationId(), ref symbol, varInfo);
                }
            }
            else
            {
                return PlcTags.TagFactory(varInfo.Name, new ItemAddress(varInfo.AccessSequence), varType.Softdatatype, is1Dim);
            }
        }

        /// <summary>
        /// Get the plc tag for the given plc tag symbol. 
        /// </summary>
        /// <param name="symbol">plc tag symbol</param>
        /// <returns>plc tag, returns null if plc tag could not be found</returns>
        public PlcTag getPlcTagBySymbol(string symbol)
        {
            VarInfo varInfo = new VarInfo();
            varInfo.Name = symbol;
            // make sure we have the db list
            if (dbInfoList == null)
            {
                if (GetListOfDatablocks(out dbInfoList) != 0) { return null; }
            }
            string levelName = parseSymbolLevel(ref symbol);
            // find db by first level name of symbol
            DatablockInfo dbInfo = dbInfoList.Find(dbi => dbi.db_name == levelName);
            if (dbInfo != null)
            {
                varInfo.AccessSequence = String.Format("{0:X}", dbInfo.db_block_relid);
                return browsePlcTagBySymbol(dbInfo.db_block_ti_relid, ref symbol, varInfo);
            }
            else
            {
                symbol = varInfo.Name;
                // Merker
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theMArea_Rid);
                PlcTag tag = browsePlcTagBySymbol(0x90030000, ref symbol, varInfo);
                if (tag != null) return tag;
                symbol = varInfo.Name;
                // Outputs
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theQArea_Rid);
                tag = browsePlcTagBySymbol(0x90020000, ref symbol, varInfo);
                if (tag != null) return tag;
                symbol = varInfo.Name;
                // Inputs
                varInfo.AccessSequence = String.Format("{0:X}", Ids.NativeObjects_theIArea_Rid);
                tag = browsePlcTagBySymbol(0x90010000, ref symbol, varInfo);
                if (tag != null) return tag;
                // TODO: implement s5timers and counters... no one uses them anymore anyway
            }
            return null;
        }

        public class BrowseEntry
        {
            public string Name;
            public uint Softdatatype;
            public UInt32 LID;
            public UInt32 SymbolCrc;
            public string AccessSequence;
        };

        public class BrowseData
        {
            public string db_name;                                          // Name of the datablock
            public UInt32 db_number;                                        // Number of the datablock
            public UInt32 db_block_relid;                                   // RID of the datablock
            public UInt32 db_block_ti_relid;                                // Type-Info RID of the datablock
            public List<BrowseEntry> variables = new List<BrowseEntry>();   // Variables inside the datablock
        };

        public class DatablockInfo
        {
            public string db_name;                                          // Name of the datablock
            public UInt32 db_number;                                        // Number of the datablock
            public UInt32 db_block_relid;                                   // RID of the datablock
            public UInt32 db_block_ti_relid;                                // Type-Info RID of the datablock
        };

        public int GetListOfDatablocks(out List<DatablockInfo> dbInfoList)
        {
            int res;

            dbInfoList = new List<DatablockInfo>();

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = Ids.NativeObjects_thePLCProgram_Rid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            // Add the attributes we need in the response
            exploreReq.AddressList.Add(Ids.ObjectVariableTypeName);

            // Set filter on Id for Datablock Class RID. With this filter, we only
            // get informations from datablocks, and not other blocks we don't need here.
            var filter = new ValueStruct(Ids.Filter);
            filter.AddStructElement(Ids.FilterOperation, new ValueDInt(8)); // 8 = InstanceIOf
            filter.AddStructElement(Ids.AddressCount, new ValueUDInt(0));
            uint[] faddress = new uint[32]; // Unknown, possible dependant on FilterOperation
            filter.AddStructElement(Ids.Address, new ValueUDIntArray(faddress));
            filter.AddStructElement(Ids.FilterValue, new ValueRID(Ids.DB_Class_Rid));

            exploreReq.FilterData = filter;

            res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            // Get the datablock information we want further informations from.
            var objList = exploreRes.Objects;

            foreach (var ob in objList)
            {
                // May be this check can be removed, if setting the filter to the DB_Class_Rid is working 100%.
                switch (ob.ClassId)
                {
                    case Ids.DB_Class_Rid:
                        UInt32 relid = ob.RelationId;
                        UInt32 area = (relid >> 16);
                        UInt32 num = relid & 0xffff;
                        if (area == 0x8a0e)
                        {
                            var name = (ValueWString)(ob.GetAttribute(Ids.ObjectVariableTypeName));
                            DatablockInfo data = new DatablockInfo();
                            data.db_block_relid = relid;
                            data.db_name = name.GetValue();
                            data.db_number = num;
                            dbInfoList.Add(data);
                        }
                        break;
                }
            }

            // Get the TypeInfo RID to RelId from the first response

            // With LID=1 we get the RID back. With this number we can explore further 
            // informations of this datablock.
            // This is neccessary, because informations about instance DBs (e.g. TON) you
            // don't get by the RID of the DB, instead of exploring the TON Type RID.
            var readlist = new List<ItemAddress>();
            var values = new List<object>();
            var errors = new List<UInt64>();

            foreach (var data in dbInfoList)
            {
                if (data.db_number > 0)
                {
                    // Insert the address
                    var adr1 = new ItemAddress();
                    adr1.AccessArea = data.db_block_relid;
                    adr1.AccessSubArea = Ids.DB_ValueActual;
                    adr1.LID.Add(1);
                    readlist.Add(adr1);
                }
            }
            res = ReadValues(readlist, out values, out errors);
            if (res != 0)
            {
                return res;
            }

            // Insert response data into the list
            for (int i = 0; i < values.Count; i++)
            {
                if (errors[i] == 0)
                {
                    var rid = (ValueRID)values[i];
                    var data = dbInfoList[i];
                    data.db_block_ti_relid = rid.GetValue();
                    dbInfoList[i] = data;
                }
                else
                {
                    // On error, set relid=0, which is then removed in the next step.
                    // Should we report this for the user?
                    var data = dbInfoList[i];
                    data.db_block_ti_relid = 0;
                    dbInfoList[i] = data;
                }
            }

            // Remove elements with db_block_ti_relid == 0.
            // This can occur on datablocks which are only in load memory and can't be explored.
            dbInfoList.RemoveAll(item => item.db_block_ti_relid == 0);

            return 0;
        }

        public int GetTypeInformation(uint exploreId, out List<PObject> objList)
        {
            int res;
            objList = new List<PObject>();

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = exploreId;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }
            objList = exploreRes.Objects;

            return 0;
        }

        /// <summary>
        /// Requests the tag and block comments from the Plc, returned as XML strings.
        /// xml_linecomment:
        /// The returned XML format differs between between request of I/Q/M/C/T areas and datablocks:
        /// I/Q/M/C/T: <CommentDictionary>     <TagLineComments>      <Comment RefID="ID"> <DictEntry Lanuage="de-DE"> ....
        /// Datablock: <InterfaceLineComments> <Part Kind="Comments"> <Comment Path="ID">  <DictEntry Lanuage="de-DE"> ....
        /// As "ID" the number for the variable identification is used.
        /// 
        /// xml_dbcomment:
        /// The xml-value description generated from our own value xml-serialization for WStringSparseArray. The value key is the language id.
        /// Example:
        /// <Value type ="WStringSparseArray"><Value key="1032">DB Kommentar in german de-DE</Value><Value key="1034">DB comment in english en-US</Value></Value>
        /// </summary>
        /// <param name="relid">The relation ID for the area you want the comments for, e.g. 0x8a0e0000+db_number, or 0x52 for M-area</param>
        /// <param name="xml_linecomment"></param>
        /// <param name="xml_dbcomment"></param>
        /// <returns>0 if no error</returns>
        public int GetCommentsXml(uint relid, out string xml_linecomment, out string xml_dbcomment)
        {
            int res;
            // With requesting DataInterface_InterfaceDescription, whe would be able to get all informations like the access ids and
            // datatype informations, that we get from the other browsing method. Needs to be tested which one is more efficient on network traffic or plc load.
            // If we keep use browsing for the comments, at least we would be able to read all information in one request.
            xml_linecomment = String.Empty;
            xml_dbcomment = String.Empty;

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = relid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;

            // We want to know the following attributes
            exploreReq.AddressList.Add(Ids.ASObjectES_Comment);
            exploreReq.AddressList.Add(Ids.DataInterface_LineComments);

            res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            foreach (var obj in exploreRes.Objects)
            {
                foreach (var att in obj.Attributes)
                {
                    switch (att.Key)
                    {
                        case Ids.ASObjectES_Comment:
                            var att_comment = (ValueWStringSparseArray)att.Value;
                            xml_dbcomment = att_comment.ToString();
                            break;
                        case Ids.DataInterface_LineComments:
                            var att_linecomment = (ValueBlobSparseArray)att.Value;
                            BlobDecompressor bd = new BlobDecompressor();
                            var blob_sp = att_linecomment.GetValue();
                            // In DBs we get the data with Sparsearray key = 1, in M-Area with key = 2.
                            // For now, just take the first, don't know where the key ids are for.
                            foreach (var key in blob_sp.Keys)
                            {
                                xml_linecomment = bd.decompress(blob_sp[key].value, 4); // Offset of 4, as we have a header for the zlib dictionary version
                                break;
                            }
                            break;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Reads the interface description and the line comments of a block or memory area in one request,
        /// both decompressed to XML. The PLC leaves the attributes out without an error when the access level
        /// of the session does not allow to read them (HMI access) or the block has no comments.
        /// </summary>
        /// <param name="relid">A block (DB, FB, UDT ...) or the I/Q/M area (80, 81, 82)</param>
        /// <param name="interfaceXml">BlockInterface or IdentContainer XML, null when the PLC sent none</param>
        /// <param name="lineCommentsXml">InterfaceLineComments or CommentDictionary XML, null when the PLC sent none</param>
        /// <returns>0 if no error</returns>
        public int GetInterfaceWithComments(uint relid, out string interfaceXml, out string lineCommentsXml)
        {
            interfaceXml = null;
            lineCommentsXml = null;

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = relid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;
            exploreReq.AddressList.Add(Ids.DataInterface_InterfaceDescription);
            exploreReq.AddressList.Add(Ids.DataInterface_LineComments);

            int res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            foreach (var obj in exploreRes.Objects)
            {
                if (obj.Attributes.TryGetValue(Ids.DataInterface_InterfaceDescription, out var desc))
                {
                    interfaceXml = DecompressBlobValue(desc);
                }
                if (obj.Attributes.TryGetValue(Ids.DataInterface_LineComments, out var comments))
                {
                    lineCommentsXml = DecompressBlobValue(comments);
                }
            }
            return 0;
        }

        /// <summary>The XML of a blob or of the first non-empty entry of a blob sparse array (key order), null for anything else.</summary>
        internal static string DecompressBlobValue(PValue value)
        {
            byte[] blob = null;
            if (value is ValueBlob b)
            {
                blob = b.GetValue();
            }
            else if (value is ValueBlobSparseArray sp)
            {
                var entries = sp.GetValue();
                foreach (var key in entries.Keys.OrderBy(k => k))
                {
                    if (entries[key].value != null && entries[key].value.Length > 4)
                    {
                        blob = entries[key].value;
                        break;
                    }
                }
            }
            if (blob == null || blob.Length <= 4)
            {
                return null;
            }
            // 4 bytes header with the version of the zlib dictionary
            return new BlobDecompressor().decompress(blob, 4);
        }

        /// <summary>Names and relation ids of all blocks of the PLC program (DBs, FBs, FCs, UDTs ...).</summary>
        public int GetBlockNames(out Dictionary<string, uint> blocks)
        {
            blocks = new Dictionary<string, uint>(StringComparer.Ordinal);

            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = Ids.NativeObjects_thePLCProgram_Rid;
            exploreReq.ExploreRequestId = Ids.None;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;
            exploreReq.AddressList.Add(Ids.ObjectVariableTypeName);

            int res = SendS7plusFunctionObject(exploreReq);
            if (res != 0)
            {
                return res;
            }
            m_LastError = 0;
            WaitForNewS7plusReceived(m_ReadTimeout);
            if (m_LastError != 0)
            {
                return m_LastError;
            }

            var exploreRes = ExploreResponse.DeserializeFromPdu(m_ReceivedPDU, true);
            res = checkResponseWithIntegrity(exploreReq, exploreRes);
            if (res != 0)
            {
                return res;
            }

            var program = exploreRes.Objects.FirstOrDefault(o => o.ClassId == Ids.PLCProgram_Class_Rid);
            if (program == null)
            {
                return 0;
            }
            foreach (var ob in program.GetObjects())
            {
                if (ob.GetAttribute(Ids.ObjectVariableTypeName) is ValueWString name &&
                    !string.IsNullOrEmpty(name.GetValue()) && !blocks.ContainsKey(name.GetValue()))
                {
                    blocks[name.GetValue()] = ob.RelationId;
                }
            }
            return 0;
        }
    }
    #endregion
}
