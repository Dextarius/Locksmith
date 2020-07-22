using System;
using System.Diagnostics;
using System.Threading;

namespace Dextarius
{
    public static partial class Locksmith
    {
        public static bool          IsWriteLocked(this int lockState) => (lockState & Mask.WriteLockState)     ==  State.WriteLockActive;
        public static bool           IsReadLocked(this int lockState) => (lockState & Mask.ActiveReaderCount)  >   State.None;
        public static bool   IsReservedForUpgrade(this int lockState) => (lockState & Mask.UpgradeReservation) ==  State.ReservedForUpgrade;
        public static bool    IsReservedForWriter(this int lockState) => (lockState & Mask.WriteReservation)   ==  State.ReservedForWriter;
        public static bool  IsAtMaxWaitingWriters(this int lockState) => (lockState & Mask.WaitingWriterCount) ==  State.MaxWaitingWriters; 
        public static bool   IsAtMaxActiveReaders(this int lockState) => (lockState & Mask.ActiveReaderCount)  ==  State.MaxActiveReaders;
        public static bool      HasWaitingReaders(this int lockState) => (lockState & Mask.WaitingReaders)     ==  State.ReadersAreWaiting;
        public static bool      HasWaitingWriters(this int lockState) => (lockState & Mask.WaitingWriterCount) >   State.None;
        public static int   NumberOfActiveReaders(this int lockState) => (lockState & Mask.ActiveReaderCount);
        public static int  NumberOfWaitingWriters(this int lockState) =>  (lockState & Mask.WaitingWriterCount)  /  State.OneWaitingWriter;
        
        private static bool       IsNotWriteLocked(this int lockState) => (lockState.IsWriteLocked() == false);
        private static bool        IsNotReadLocked(this int lockState) => (lockState.IsReadLocked() == false);
        public static bool IsNotReservedForUpgrade(this int lockState) => (lockState.IsReservedForUpgrade() == false);
        private static bool IsNotReservedByAWriter(this int lockState) => (lockState.IsReservedForWriter() == false);
        private static bool    HasNoWaitingWriters(this int lockState) => (lockState.HasWaitingWriters() == false);
        private static bool    HasNoWaitingReaders(this int lockState) => (lockState.HasWaitingReaders() == false);
        
        private static bool HasLessThanMaxActiveReaders(this int lockState) => (lockState & Mask.ActiveReaderCount) < State.MaxActiveReaders;
        private static bool    IsNotAtMaxWaitingWriters(this int lockState) => lockState.IsAtMaxWaitingWriters() == false;
        private static bool    IsReservedOrWriterActive(this int lockState) =>  lockState.IsWriteLocked() || 
                                                                               (lockState.IsReadLocked() &&
                                                                                  (lockState.IsReservedForUpgrade() ||
                                                                                   lockState.IsReservedForWriter()));
#if DEBUG
        
        private static bool HasOnlyOneActiveReader(this int lockState) => (lockState & Mask.ActiveReaderCount) == State.OneActiveReader;
        private static bool ActiveReaderCountIsAtLeast(this int lockState, int minimum) => 
            (lockState & Mask.ActiveReaderCount) >=  (State.OneActiveReader * minimum);
        
        private static bool WaitingWriterCountIsAtLeast(this int lockState, int minimum) => 
            (lockState & Mask.WaitingWriterCount) >=  (State.OneWaitingWriter * minimum);


        
        private static bool HasOneFewerWaitingWritersThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.WaitingWriterCount) == 
           ((stateToCompareAgainst   & Mask.WaitingWriterCount) - State.OneWaitingWriter) &&
            ((stateOfLockBeingChecked & Mask.WaitingWriterCount) < (stateToCompareAgainst & Mask.WaitingWriterCount));


        private static bool HasSameNumberOfWaitingWritersAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.WaitingWriterCount) ==  (stateToCompareAgainst & Mask.WaitingWriterCount);

        private static bool HasSameWaitingReadersStatusAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.WaitingReaders) ==  (stateToCompareAgainst & Mask.WaitingReaders);
        
        private static bool HasSameNumberOfActiveReadersAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.ActiveReaderCount) ==  (stateToCompareAgainst & Mask.ActiveReaderCount);
        
        private static bool HasSameWriteLockStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.WriteLockState) ==  (stateToCompareAgainst & Mask.WriteLockState);
        
        private static bool HasSameWriteReservationStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.WriteReservation) ==  (stateToCompareAgainst & Mask.WriteReservation);
        
        private static bool HasSameUpgradeReservationStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.UpgradeReservation) ==  (stateToCompareAgainst & Mask.UpgradeReservation);
        
        private static bool HasOneMoreActiveReaderThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.ActiveReaderCount) ==  
           ((stateToCompareAgainst   & Mask.ActiveReaderCount) + State.OneActiveReader) && 
            ((stateOfLockBeingChecked & Mask.ActiveReaderCount) > (stateToCompareAgainst & Mask.ActiveReaderCount));
        
        private static bool HasOneLessActiveReaderThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.ActiveReaderCount) ==  
            ((stateToCompareAgainst & Mask.ActiveReaderCount) - State.OneActiveReader) && 
            ((stateOfLockBeingChecked & Mask.ActiveReaderCount) < (stateToCompareAgainst & Mask.ActiveReaderCount));
        
        private static bool HasOneMoreWaitingWriterThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            (stateOfLockBeingChecked & Mask.WaitingWriterCount) ==  
            ((stateToCompareAgainst & Mask.WaitingWriterCount) + State.OneWaitingWriter) && 
            ((stateOfLockBeingChecked & Mask.WaitingWriterCount) > (stateToCompareAgainst & Mask.WaitingWriterCount));
        
        

        private static void AssertReservedForWriterFlag_Off(this int lockState) => 
            Debug.Assert((lockState.IsNotReservedByAWriter()), 
                $"The reserved for writer flag should have been off. " + OutputInvalidState(lockState));
        private static void AssertReservedForWriterFlag_On(this int lockState) => 
            Debug.Assert((lockState.IsReservedForWriter()), 
                $"The reserved for writer flag should have been on. " + OutputInvalidState(lockState));
        
        private static void AssertReservedForUpgradeFlag_Off(this int lockState) => 
            Debug.Assert((lockState.IsNotReservedForUpgrade()), 
                $"The reserved for upgrade flag should not have been active. " + OutputInvalidState(lockState));
        
        private static void AssertReservedForUpgradeFlag_On(this int lockState) => 
            Debug.Assert((lockState.IsReservedForUpgrade()), 
                $"The reserved for upgrade flag should have been active. " + OutputInvalidState(lockState));

        private static void AssertWriteLockFlag_Off(this int lockState) => 
            Debug.Assert(lockState.IsNotWriteLocked(), 
                $"The writer lock active flag should have been off. " + OutputInvalidState(lockState));
        
        private static void AssertWriteLockFlag_On(this int lockState) => 
            Debug.Assert(lockState.IsWriteLocked(), 
                $"The writer lock active flag should have been off. " + OutputInvalidState(lockState));
        
        private static void AssertActiveReaderCount_IsAtLeast(this int lockState, int minimum) => 
            Debug.Assert(lockState.ActiveReaderCountIsAtLeast(minimum), 
                $"The active reader count should have been at least {minimum}. " + OutputInvalidState(lockState));

        private static void AssertActiveReaderCount_LessThanMax(this int lockState) => 
            Debug.Assert(lockState.HasLessThanMaxActiveReaders(), 
                $"The active reader count should have been less than the maximum, which is {MaxNumberOfActiveReaders}. " + 
                OutputInvalidState(lockState));

        private static void AssertActiveReaderCount_IsOne(this int lockState) => 
            Debug.Assert(lockState.HasOnlyOneActiveReader(), 
                $"The active reader count should have been one. " + OutputInvalidState(lockState));

        private static void AssertActiveReaderCount_IsZero(this int lockState) => 
            Debug.Assert(lockState.IsNotReadLocked(), 
                $"The active reader count should have been zero. " + OutputInvalidState(lockState));
        
        private static void AssertWaitingWriterCount_IsZero(this int lockState) => 
            Debug.Assert(lockState.HasNoWaitingWriters(), 
                $"The waiting writer count should have been zero. " + OutputInvalidState(lockState));
        
        private static void AssertWaitingWriterCount_IsAtLeast(this int lockState, int minimum) => 
            Debug.Assert(lockState.WaitingWriterCountIsAtLeast(minimum), 
                $"The waiting writer count should have been at least {minimum}. " + OutputInvalidState(lockState));
        
        private static void AssertWaitingWriterCount_NotAtMax(this int lockState) => 
            Debug.Assert(lockState.IsNotAtMaxWaitingWriters(), 
                $"The waiting writer count should have been less than the maximum , which is {MaxNumberOfWaitingWriters}. " +
                OutputInvalidState(lockState));
        
        private static void AssertWaitingWriterCount_AtMax(this int lockState) => 
            Debug.Assert(lockState.IsAtMaxWaitingWriters(), 
                $"The waiting writer count should have at the maximum, which is {MaxNumberOfWaitingWriters}. " + 
                OutputInvalidState(lockState));
        
        private static void AssertWaitingReadersFlag_Off(this int lockState) => 
            Debug.Assert(lockState.HasNoWaitingReaders(), 
                $"The waiting reader flag should have been off. " + OutputInvalidState(lockState));
        
        private static void AssertWaitingReadersFlag_On(this int lockState) => 
            Debug.Assert(lockState.HasWaitingReaders(), 
                $"The waiting reader flag should have been on. " + OutputInvalidState(lockState));
        
        private static void AssertReservedOrWriterActive(this int lockState) => 
            Debug.Assert(lockState.IsReservedOrWriterActive(), 
                $"Either the write lock, or the active reader and reserved flags should have been on. " + 
                OutputInvalidState(lockState));
        
        private static void AssertHasSameWaitingReadersStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasSameWaitingReadersStatusAs(stateToCompareAgainst), 
                $"The waiting readers flag for the checked state was expected to be the same as the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertHasSameWriteReservationStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasSameWriteReservationStateAs(stateToCompareAgainst), 
                $"The reserved for writer flag for the checked state was expected to be the same as the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertHasSameUpgradeReservationStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasSameUpgradeReservationStateAs(stateToCompareAgainst), 
                $"The reserved for upgrade flag for the checked state was expected to be the same as the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertHasSameNumberOfWaitingWritersAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasSameNumberOfWaitingWritersAs(stateToCompareAgainst), 
                $"The waiting writer count for the checked state was expected to be the same as the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertHasSameNumberOfActiveReadersAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasSameNumberOfActiveReadersAs(stateToCompareAgainst), 
                $"The active reader count for the checked state was expected to be the same as the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertHasSameWriteLockStateAs(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasSameWriteLockStateAs(stateToCompareAgainst), 
                $"The write lock state for the checked state was expected to be the same as the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertHasOneFewerWaitingWritersThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasOneFewerWaitingWritersThan(stateToCompareAgainst), 
                $"The waiting writer count for the checked state was expected to be one less than the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        private static void AssertHasOneMoreWaitingWriterThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasOneMoreWaitingWriterThan(stateToCompareAgainst), 
                $"The waiting writer count for the checked state was expected to be one less than the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertActiveReaderCount_OneHigherThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasOneMoreActiveReaderThan(stateToCompareAgainst), 
                $"The active reader count for the checked state was expected to be one more than the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        private static void AssertActiveReaderCount_OneLessThan(this int stateOfLockBeingChecked, int stateToCompareAgainst) => 
            Debug.Assert(stateOfLockBeingChecked.HasOneLessActiveReaderThan(stateToCompareAgainst), 
                $"The active reader count for the checked state was expected to be one more than the compared state. " + 
                OutputInvalidState(stateOfLockBeingChecked, stateToCompareAgainst));
        
        
        private static void AssertActiveReadersIsAtMaxOrOtherFlagIsActive(this int lockState) =>
            Debug.Assert(lockState.IsWriteLocked()        || lockState.IsReservedForWriter()  ||
                         lockState.HasWaitingWriters()    || lockState.IsAtMaxActiveReaders() ||
                        (lockState.IsReservedForUpgrade() && lockState.IsReadLocked()),
                        $"The active reader count should have been at max, or another flag should have been active. " + 
                        OutputInvalidState(lockState));


        private static string OutputInvalidState(int lockState) =>
            $"Invalid lock state was: {Convert.ToString(lockState, 16)}";
        
        private static string OutputInvalidState(int invalidLockState, int comparedState) =>
            $"Invalid lock state was: {Convert.ToString(invalidLockState, 16)}, " +
            $"Compared lock state was {Convert.ToString(comparedState, 16)}.  ";
        
#endif
    }
}