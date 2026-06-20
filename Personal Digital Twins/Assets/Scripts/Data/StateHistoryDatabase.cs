using System;
using System.Collections.Generic;

namespace LifeOS.Data
{
    [Serializable]
    public class StateHistoryDatabase
    {
        public LifeState currentState = LifeState.CreateDefault();
        public List<StateSnapshot> snapshots = new List<StateSnapshot>();
    }
}
