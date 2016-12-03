﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tera.Game;

namespace Data.Events.Abnormality
{
    public class AbnormalityEvent : Event
    {
        public List<int> Ids { get; set; }

      
        public int RemainingSecondBeforeTrigger { get; set; }
        public int RewarnTimeoutSeconds { get; set; }
        public List<HotDot.Types> Types { get; set; }
        public AbnormalityTargetType Target { get; set; }
        public AbnormalityTriggerType Trigger { get; set; }
        public bool BossAttackedBySelf { get; set; }
        public AbnormalityEvent(bool inGame, bool active, int priority, Dictionary<int,int> areaBossBlackList, List<int> ids, List<HotDot.Types> types, AbnormalityTargetType target, AbnormalityTriggerType trigger, int remainingSecondsBeforeTrigger, int rewarnTimeoutSecounds, bool attackedBySelf): base(inGame, active, priority, areaBossBlackList)
        {
            Types = types;
            Ids = ids;
            Target = target;
            Trigger = trigger;
            RemainingSecondBeforeTrigger = remainingSecondsBeforeTrigger;
            RewarnTimeoutSeconds = rewarnTimeoutSecounds;
            BossAttackedBySelf = attackedBySelf;
        }
    }
}
