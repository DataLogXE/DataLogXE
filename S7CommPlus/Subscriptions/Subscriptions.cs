#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 *
 * Subscription object layout based on S7CommPlusDriver (Subscriptions/Subscription.cs) by Thomas Wiens.
 /****************************************************************************/
#endregion

namespace S7CommPlusDriver
{
    public partial class S7CommPlusConnection
    {
        // RouteMode 0x20 with CreditLimit -1: all values on create, then only changed values per cycle,
        // no notification on cycles without change, no credit refresh needed.
        private const byte SubscriptionRouteMode = 0x20;
        private const short SubscriptionCreditLimit = -1;
        private uint m_NextSubscriptionRelationId = 0x7fffc001;

        /// <summary>
        /// Creates a data change subscription. Item reference ids in the notifications are the
        /// 1-based positions in <paramref name="items"/>.
        /// </summary>
        /// <param name="items">CRC secured addresses</param>
        /// <param name="cycleTimeMs">sampling cycle, the PLC accepts 100 ms and more</param>
        /// <param name="subscriptionObjectId">object id to route the notifications and to delete the subscription</param>
        /// <param name="plcReturnValue">raw return value of the PLC when it refused the subscription</param>
        /// <returns>0 or an error code; errCliFunctionRefused when the PLC refused it</returns>
        public int SubscriptionCreate(IReadOnlyList<ItemAddress> items, uint cycleTimeMs, out uint subscriptionObjectId, out ulong plcReturnValue)
        {
            subscriptionObjectId = 0;
            plcReturnValue = 0;
            if (items == null || items.Count == 0)
            {
                return S7Consts.errCliInvalidParams;
            }

            uint relationId = m_NextSubscriptionRelationId++;
            if (m_NextSubscriptionRelationId >= 0x7fffffff)
            {
                m_NextSubscriptionRelationId = 0x7fffc001;
            }

            var subsobj = new PObject();
            subsobj.ClassId = Ids.ClassSubscription;
            subsobj.RelationId = relationId;
            subsobj.AddAttribute(Ids.ObjectVariableTypeName, new ValueWString("Subscription_" + relationId.ToString()));
            subsobj.AddAttribute(Ids.SubscriptionFunctionClassId, new ValueUSInt(0));
            subsobj.AddAttribute(Ids.SubscriptionMissedSendings, new ValueUInt(0));
            subsobj.AddAttribute(Ids.SubscriptionSubsystemError, new ValueLInt(0));
            subsobj.AddAttribute(Ids.SubscriptionRouteMode, new ValueUSInt(SubscriptionRouteMode));
            subsobj.AddAttribute(Ids.SubscriptionActive, new ValueBool(true));
            subsobj.AddAttribute(Ids.SubscriptionReferenceList, BuildSubscriptionReferenceList(items, 1));
            subsobj.AddAttribute(Ids.SubscriptionCycleTime, new ValueUDInt(cycleTimeMs));
            subsobj.AddAttribute(Ids.SubscriptionDisabled, new ValueUSInt(0));
            subsobj.AddAttribute(Ids.SubscriptionCount, new ValueUSInt(0));
            subsobj.AddAttribute(Ids.SubscriptionCreditLimit, new ValueInt(SubscriptionCreditLimit));
            subsobj.AddAttribute(Ids.SubscriptionTicks, new ValueUInt(65535));

            var createObjReq = new CreateObjectRequest(ProtocolVersion.V2, 0, true);
            createObjReq.TransportFlags = 0x34;
            createObjReq.RequestId = m_SessionId2;
            createObjReq.RequestValue = new ValueUDInt(0);
            createObjReq.SetRequestObject(subsobj);

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
                m_log.Error("SubscriptionCreate: invalid CreateObjectResponse");
                return S7Consts.errIsoInvalidPDU;
            }
            uint objectId = createObjRes.ObjectIds != null && createObjRes.ObjectIds.Count > 0 ? createObjRes.ObjectIds[0] : 0;
            if (createObjRes.ReturnValue != 0)
            {
                plcReturnValue = createObjRes.ReturnValue;
                m_log.Warning("SubscriptionCreate: refused by the PLC, return value 0x{ReturnValue:X16}", createObjRes.ReturnValue);
                // The PLC may still have created the object: remove it again.
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

        /// <summary>Deletes a subscription created by <see cref="SubscriptionCreate"/>.</summary>
        public int SubscriptionDelete(uint subscriptionObjectId)
        {
            if (subscriptionObjectId == 0 || subscriptionObjectId == m_SessionId)
            {
                return S7Consts.errCliInvalidParams;
            }
            return DeleteObject(subscriptionObjectId);
        }

        /// <summary>
        /// Reference list (attribute 1048) as address array:
        /// header [0x8000ccnn (cc = change counter), unsubscribe count, subscribe count],
        /// per item [0x8004nnnn (nnnn = 1 + LIDs), ref id, 0, area, crc, subarea, LIDs...].
        /// </summary>
        internal static ValueUDIntArray BuildSubscriptionReferenceList(IReadOnlyList<ItemAddress> items, byte changeCounter)
        {
            var list = new List<uint>(3 + items.Count * 8)
            {
                0x80000000u | ((uint)changeCounter << 16),
                0,
                (uint)items.Count,
            };
            uint refId = 1;
            foreach (var adr in items)
            {
                list.Add(0x80040000u | (uint)((1 + adr.LID.Count) & 0xffff));
                list.Add(refId++);
                list.Add(0);
                list.Add(adr.AccessArea);
                list.Add(adr.SymbolCrc);
                list.Add(adr.AccessSubArea);
                list.AddRange(adr.LID);
            }
            return new ValueUDIntArray(list.ToArray(), 0x20);
        }
    }
}
