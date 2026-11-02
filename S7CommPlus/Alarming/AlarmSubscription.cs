#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 *
 * Alarm subscription object and active alarm request based on S7CommPlusDriver
 * (Alarming/AlarmsHandler.cs, Alarming/BrowseAlarms.cs) by Thomas Wiens.
 /****************************************************************************/
#endregion

using S7CommPlusDriver.Alarming;

namespace S7CommPlusDriver
{
    public partial class S7CommPlusConnection
    {
        // The relation id of the alarm subscription reference must be unique on the CPU: a second
        // subscription with the same id, from any session, is refused (0x8013A50003E0FFC4 on a 1512SP).
        // Clients do not know each other's ids, so it is random and a refused create tries another one.
        private const uint AlarmRefRelationIdBase = 0x51010000;
        private const int AlarmSubscriptionCreateAttempts = 3;

        /// <summary>
        /// Subscribes to every alarm of the PLC (all domains) with the texts of <paramref name="languages"/>.
        /// The PLC sends one notification per alarm change; route them by <paramref name="subscriptionObjectId"/>
        /// and decode them with <see cref="AlarmDai.FromNotification"/>. Uses one PLC subscription, no value attributes.
        /// </summary>
        /// <param name="languages">language ids (e.g. 0x0407 de-DE); the PLC may send more</param>
        /// <param name="plcReturnValue">raw return value of the PLC when it refused the subscription</param>
        /// <returns>0 or an error code; errCliFunctionRefused when the PLC refused it</returns>
        public int AlarmSubscriptionCreate(IReadOnlyList<uint> languages, out uint subscriptionObjectId, out ulong plcReturnValue)
        {
            int res = 0;
            subscriptionObjectId = 0;
            plcReturnValue = 0;
            for (int attempt = 0; attempt < AlarmSubscriptionCreateAttempts; attempt++)
            {
                uint refRelationId = AlarmRefRelationIdBase + (uint)Random.Shared.Next(1, 0x10000);
                res = AlarmSubscriptionCreateOnce(languages, refRelationId, out subscriptionObjectId, out plcReturnValue);
                if (res != S7Consts.errCliFunctionRefused)
                {
                    return res;
                }
            }
            return res;
        }

        private int AlarmSubscriptionCreateOnce(IReadOnlyList<uint> languages, uint refRelationId, out uint subscriptionObjectId, out ulong plcReturnValue)
        {
            subscriptionObjectId = 0;
            plcReturnValue = 0;

            uint relationId = m_NextSubscriptionRelationId++;
            if (m_NextSubscriptionRelationId >= 0x7fffffff)
            {
                m_NextSubscriptionRelationId = 0x7fffc001;
            }

            var createObjReq = new CreateObjectRequest(ProtocolVersion.V2, 0, true);
            createObjReq.TransportFlags = 0x34;
            createObjReq.RequestId = m_SessionId2;
            createObjReq.RequestValue = new ValueUDInt(0);
            createObjReq.SetRequestObject(BuildAlarmSubscriptionObject(relationId, refRelationId, languages));

            int res = SendS7plusFunctionObject(createObjReq);
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

            var createObjRes = CreateObjectResponse.DeserializeFromPdu(m_ReceivedPDU);
            if (createObjRes == null)
            {
                m_log.Error("AlarmSubscriptionCreate: invalid CreateObjectResponse");
                return S7Consts.errIsoInvalidPDU;
            }
            uint objectId = createObjRes.ObjectIds != null && createObjRes.ObjectIds.Count > 0 ? createObjRes.ObjectIds[0] : 0;
            if (createObjRes.ReturnValue != 0)
            {
                plcReturnValue = createObjRes.ReturnValue;
                m_log.Warning("AlarmSubscriptionCreate: refused by the PLC, return value 0x{ReturnValue:X16}", createObjRes.ReturnValue);
                if (objectId != 0)
                {
                    DeleteObject(objectId);
                }
                return S7Consts.errCliFunctionRefused;
            }
            if (objectId == 0)
            {
                return S7Consts.errIsoInvalidPDU;
            }
            subscriptionObjectId = objectId;
            return 0;
        }

        /// <summary>Deletes an alarm subscription created by <see cref="AlarmSubscriptionCreate"/>.</summary>
        public int AlarmSubscriptionDelete(uint subscriptionObjectId) => SubscriptionDelete(subscriptionObjectId);

        /// <summary>
        /// Reads the alarms the PLC holds right now (active, or gone but still to be acknowledged), with the
        /// same attributes as an alarm notification.
        /// </summary>
        public int ReadActiveAlarms(out List<AlarmDai> alarms)
        {
            alarms = new List<AlarmDai>();
            var exploreReq = new ExploreRequest(ProtocolVersion.V2);
            exploreReq.ExploreId = Ids.NativeObjects_theAlarmSubsystem_Rid;
            exploreReq.ExploreRequestId = Ids.AlarmSubsystem_itsUpdateRelevantDAI;
            exploreReq.ExploreChildsRecursive = 1;
            exploreReq.ExploreParents = 0;
            exploreReq.AddressList.Add(Ids.ObjectVariableTypeName);
            exploreReq.AddressList.Add(Ids.DAI_CPUAlarmID);
            exploreReq.AddressList.Add(Ids.DAI_AllStatesInfo);
            exploreReq.AddressList.Add(Ids.DAI_AlarmDomain);
            exploreReq.AddressList.Add(Ids.DAI_Coming);
            exploreReq.AddressList.Add(Ids.DAI_Going);
            exploreReq.AddressList.Add(Ids.DAI_MessageType);
            exploreReq.AddressList.Add(Ids.DAI_HmiInfo);
            exploreReq.AddressList.Add(Ids.DAI_SequenceCounter);
            exploreReq.AddressList.Add(Ids.DAI_AlarmTexts_Rid);

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
            foreach (var obj in exploreRes.Objects ?? new List<PObject>())
            {
                if (obj != null && obj.ClassId == Ids.DAI_Class_Rid)
                {
                    alarms.Add(AlarmDai.FromObject(obj));
                }
            }
            return 0;
        }

        /// <summary>
        /// Subscription object for alarms: function class 2, route mode 2, no credit limit (-1, the PLC then sends
        /// every change without credit refresh), all alarm domains (65535) and the texts sent with each alarm.
        /// </summary>
        internal static PObject BuildAlarmSubscriptionObject(uint relationId, uint refRelationId, IReadOnlyList<uint> languages)
        {
            var subsobj = new PObject();
            subsobj.ClassId = Ids.ClassSubscription;
            subsobj.RelationId = relationId;
            subsobj.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString("Subscription_" + relationId.ToString()));
            subsobj.AddAttribute(Ids.SubscriptionFunctionClassId, new ValueUSInt(2));
            subsobj.AddAttribute(Ids.SubscriptionMissedSendings, new ValueUInt(0));
            subsobj.AddAttribute(Ids.SubscriptionSubsystemError, new ValueLInt(0));
            subsobj.AddAttribute(Ids.SubscriptionRouteMode, new ValueUSInt(2));
            subsobj.AddAttribute(Ids.SubscriptionActive, new ValueBool(true));
            subsobj.AddAttribute(Ids.SubscriptionReferenceList, new ValueUDIntArray(new uint[] { 0x80010000, 0, 0 }, 0x20));
            subsobj.AddAttribute(Ids.SubscriptionCycleTime, new ValueUDInt(0));
            subsobj.AddAttribute(Ids.SubscriptionDelayTime, new ValueUDInt(0));
            subsobj.AddAttribute(Ids.SubscriptionDisabled, new ValueUSInt(0));
            subsobj.AddAttribute(Ids.SubscriptionCount, new ValueUSInt(0));
            subsobj.AddAttribute(Ids.SubscriptionCreditLimit, new ValueInt(SubscriptionCreditLimit));
            subsobj.AddAttribute(Ids.SubscriptionTicks, new ValueUInt(65535));

            var asref = new PObject();
            asref.ClassId = Ids.AlarmSubscriptionRef_Class_Rid;
            asref.RelationId = refRelationId;
            asref.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString("DataLogXE_Alarms"));
            asref.AddAttribute(Ids.SubscriptionReferenceTriggerAndTransmitMode, new ValueUSInt(3));
            asref.AddAttribute(Ids.AlarmSubscriptionRef_AlarmDomain, new ValueUIntArray(new ushort[10], 0x10));
            asref.AddAttribute(Ids.AlarmSubscriptionRef_AlarmDomain2, new ValueUIntArray(new ushort[] { 65535 }, 0x20));
            asref.AddAttribute(Ids.AlarmSubscriptionRef_AlarmTextLanguages_Rid, new ValueUDIntArray((languages ?? Array.Empty<uint>()).ToArray(), 0x20));
            asref.AddAttribute(Ids.AlarmSubscriptionRef_SendAlarmTexts_Rid, new ValueBool(true));
            asref.AddRelation(Ids.AlarmSubscriptionRef_itsAlarmSubsystem, Ids.NativeObjects_theAlarmSubsystem_Rid);
            subsobj.AddObject(asref);
            return subsobj;
        }
    }
}
