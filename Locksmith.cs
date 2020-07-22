/*- Note : This class was designed after reading the article:
           "Creating High-Performance Locks and Lock-free Code" written by Adam Milazzo.
           (link http://www.adammil.net/blog/v111_Creating_High-Performance_Locks_and_Lock-free_Code_for_NET_.html
           The article is quite good, and this code follows the same general the idea, so it's worth a read if you 
           want a better understanding of the mindset the code was written in. 
-*/

//- WARNING : This code is ugly, trust me I hate it, but the goal was to make it performant.
//            There is also a ton of comments and debugging code mixed in, since it's rather
//            important to understand the exact state at any given moment.  If you want to read
//            the code without all of the clutter, there is a copy in Locksmith_Concise.cs
//            that has all the debugging/extraneous comments cut out.  It's 600 lines shorter...

using System;
 using System.Diagnostics;
 using System.Threading;

namespace Dextarius
{
    /// <summary>
    ///     A static class designed to allow non-reentrant read/write locking on objects in a
    ///     manner similar to Monitor.Enter()/Exit(), using an int and two lock objects. 
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Users of this class are expected to store three pieces of data which constitute a read/write lock:
    ///         1. An int used to track the state of the lock
    ///            (henceforth referred to as the 'lock state variable')
    ///         2. An object which prospective readers can lock on.
    ///         3. An object which prospective writers can lock on.
    ///     </para>
    ///     <para>
    ///     The class uses the value of Item 1 to maintain the state of the lock, so its
    ///     value should not be changed by any process outside this class.
    ///     </para>
    ///     <para>
    ///     Items 2 and 3 must be objects which will not be used by any other lock,
    ///     whether that be the generic lock() statement, Monitor.Enter(), Monitor.Exit(),
    ///     Monitor.Wait(), etc.
    ///     </para>
    ///     <para>
    ///     Waiting threads are synchronized using Items 2 and 3, so they must be the same
    ///     objects every time you use a particular lock state variable.
    ///     </para>
    ///     <para>
    ///     Items 2 and 3 should not be the same object, and you should
    ///     not try to reuse the objects by having two locks that share the
    ///     same objects but different lock state variables.
    ///     </para>
    /// </remarks>
    public static partial class Locksmith
    {
        #region Constants

        private const int Long_Sleep_Duration = 10;
        private const int Number_Of_Spins     = 15;
        
        /*- These two classes serves as pseudo-enums.  Because the base class that enums
            inherit from is not a reference type, they cannot be used with Interlocked
            methods. These classes allow me to retain the readability benefits of the
            enum naming scheme, while still being able to use ints. -*/
        private static class Mask
        {
            //- REMINDER : Most of the code depends on the order of these flags relative to each other,
            //  if you change that order, you need to check every constant and condition that uses them.
            //- We don't use the negative bit because it makes checking the state more complicated.
            internal const int   
                WriteLockState                          =  0b0100_0000_0000_0000_0000_0000_0000_0000,
                UpgradeReservation                      =  0b0010_0000_0000_0000_0000_0000_0000_0000,
                WriteReservation                        =  0b0001_0000_0000_0000_0000_0000_0000_0000,
                WaitingWriterCount                      =  0b0000_1111_1111_0000_0000_0000_0000_0000,
                WaitingReaders                          =  0b0000_0000_0000_1000_0000_0000_0000_0000, 
                ActiveReaderCount                       =  0b0000_0000_0000_0111_1111_1111_1111_1111,
                ExcludeWriteReservation                 =  ~WriteReservation,
                WriteLockedOrActiveReaders              =   WriteLockState   | ActiveReaderCount,
                ExcludeReservationsAndActiveReaderCount = ~(WriteReservation | UpgradeReservation | ActiveReaderCount);
        }
        
        private static class State
        {
            internal const int 
                None               = 0,
                WriteLockActive    = Mask.WriteLockState, 
                ReservedForUpgrade = Mask.UpgradeReservation,
                ReservedForWriter  = Mask.WriteReservation,
                MaxWaitingWriters  = Mask.WaitingWriterCount, 
                MaxActiveReaders   = Mask.ActiveReaderCount, 
                ReadersAreWaiting  = Mask.WaitingReaders,
                
                //- As long as we don't change the order of the flags, we can change
                // the number of bits in the flags and these should stay up to date
                OneActiveReader  = (Mask.WaitingReaders - MaxActiveReaders), 
                OneWaitingWriter = (Mask.WaitingReaders + Mask.ActiveReaderCount + 1);
        }

        #endregion

        #region Fields

        private static int maxWaitTime = 10_000;

        #endregion
        

        #region Properties

        public static int MaxNumberOfWaitingWriters => State.MaxWaitingWriters / State.OneWaitingWriter;
        public static int MaxNumberOfActiveReaders  => State.MaxActiveReaders;

        /// <summary>
        ///     Controls the maximum amount of time a thread will wait for a notification before
        ///     waking up and trying to acquire the lock again.  The default is 10,000 milliseconds.
        /// </summary>
        internal static int MaxWaitTime
        {
            get => maxWaitTime;
            set => Interlocked.Exchange(ref maxWaitTime, value);
        }

        #endregion
        
        
        #region Methods
        
        /// <summary>
        ///     Acquires a read lock on the given lock state variable.
        /// </summary>
        /// <param name="lockState">       The variable that stores the state for the lock.                          </param>
        /// <param name="readLockObject">  The object used by the given lock state variable to synchronize readers.  </param>
        public static void EnterReadLock(ref int lockState, object readLockObject)
        {
            if (readLockObject == null) throw new ArgumentNullException(nameof(readLockObject));

            int spinCount = 0;

            while (true)
            {
                int formerState = lockState;
                int newState;

                //- If none of the bits higher than Mask.ActiveReaderCount are set then it's free,
                //  or it's read locked and still below the max number of concurrent readers.
                
                //- This check is the reason that the waiting readers flag was changed to use the bit
                //  after the the active reader count bits. It originally used the bit before the count,
                //  and if we had kept it that way, this check wouldn't stop new readers from entering when
                //  there were waiting readers.
                //
                //  Waiting readers are only released when the last reader exits.  Given that it would take a
                //  massive amount of incoming readers to reach the reader cap, if we assume that inflow continues,
                //  and if new readers could enter every time the active reader count goes down by 1, then any readers
                //  that wait will never be released until the inflow of readers stops. Considering the max active reader
                //  count is upwards of 250,000 threads, I don't know why that would ever happen, but I don't see any reason
                //  not to eliminate the possibility entirely.  
                if (formerState < State.MaxActiveReaders) 
                {
#if DEBUG
                    /*- If we're here:
                        There should not be an active writer.
                        The upgrade slot should not be reserved.
                        The write slot should not be reserved.
                        There should be no waiting writers.
                        There should be no waiting readers.
                        The number of active  readers can be any number from 0 to one less than the max. 
                    -*/
                    
                    formerState.AssertWriteLockFlag_Off();
                    formerState.AssertReservedForUpgradeFlag_Off();
                    formerState.AssertReservedForWriterFlag_Off();
                    formerState.AssertWaitingReadersFlag_Off();
                    formerState.AssertWaitingWriterCount_IsZero();
                    formerState.AssertActiveReaderCount_LessThanMax();
#endif
                    newState = formerState + State.OneActiveReader;
                    
                    //- If we can claim the reader slot
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) == formerState)
                    {
#if DEBUG
                        //- The only change should be that the new lock state has one more active reader.
                        newState.AssertWriteLockFlag_Off();
                        newState.AssertReservedForWriterFlag_Off();
                        newState.AssertReservedForUpgradeFlag_Off();
                        newState.AssertWaitingWriterCount_IsZero();
                        newState.AssertWaitingReadersFlag_Off();
                        newState.AssertActiveReaderCount_OneHigherThan(formerState);
#endif
                        return; //- We've entered the read lock.
                    }

                    //- lockState must have been changed before we could set
                    //  the new state, so we'll wait a tiny bit.
                    Threading.BriefWait();
                    //- The article at the top of this file mentioned that adding a brief wait helped
                    //  with performance in some cases for their lock.  We'll need to run our own tests,
                    //  but until then I don't see any harm in adding one as well.
                }
                /*- If we're here:
                    The lockState is > State.MaxActiveReaders, so either we've hit the 
                    active reader limit, or one (or more) of the bit flags after 
                    Mask.ActiveReaderCount is set.  This means there's at least one 
                    active/reserved/waiting writer or upgrading/waiting reader.  
                -*/
                //- If we haven't spun much yet..
                else if (spinCount < Number_Of_Spins)
                {
#if DEBUG
                    /*- At this point:
                        A writer may be active.
                        A reader may have reserved the upgrade slot.
                        A writer may have reserved the write slot.
                        The number of waiting writers may be any number from 0 to the max.
                        If there are reserved/waiting writers, the active reader count may be 
                        any number from 0 to Mask.ActiveReaderCount. 
                        If there are no writers involved, readers are waiting, or a reader is 
                        trying to upgrade, or the number of active readers is the max allowed 
                        by Mask.ActiveReaderCount. 
                    -*/
                    
                    lockState.AssertActiveReadersIsAtMaxOrOtherFlagIsActive();
                    
                    //- Even though the only time the lock is reserved for a writer is during
                    //  a read lock, we can't assert that there is an active reader, because
                    //  we may be at a point where the reader has exited, but the writer
                    //  still hasn't woken up yet to claim the reservation. On the other hand,
                    //  the upgrade process disables the reserved for upgrade flag at the same
                    //  time it converts the reader into a writer, so if it's flagged there
                    //  should still be a reader.
#endif
                    
                    //- Spin me right round baby, right round... 
                    Threading.Spin(spinCount++);
                }
                //- We've spun a bunch, so it's time to wait...
                else
                {
#if DEBUG
                    //- At this point: The same rules apply as the spin section. 
                    lockState.AssertActiveReadersIsAtMaxOrOtherFlagIsActive();
                    Debug.Assert(spinCount >= Number_Of_Spins);
#endif
                    
                    newState = (formerState | State.ReadersAreWaiting);
                    
                    if ((formerState & Mask.WaitingReaders)  ==  State.ReadersAreWaiting ||
                        Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState) 
                    {
#if DEBUG
                        /*- At this point:
                            A writer may be active.
                            A writer may have reserved the write slot.
                            The number of waiting writers may be any number from 0 to Mask.WaitingWriterCount.
                            If there are no  writers involved, the number of active readers is the max allowed by Mask.ActiveReaderCount 
                            If there are any writers involved, it may be any number from 0 to Mask.ActiveReaderCount. 
                            We have spun the required number of times.  
                        -*/
                        
                        newState.AssertWaitingReadersFlag_On();
                        newState.AssertHasSameWriteLockStateAs(formerState);
                        newState.AssertHasSameUpgradeReservationStateAs(formerState);
                        newState.AssertHasSameWriteReservationStateAs(formerState);
                        newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
                        newState.AssertHasSameNumberOfActiveReadersAs(formerState);
#endif
                        lock (readLockObject)
                        {
                            //- This intentionally checks lockState and not formerState
                            //  as we need to know the state after readLockObject is locked. 
                            if ((lockState & Mask.WaitingReaders)  ==  State.ReadersAreWaiting)
                            {
                                spinCount = 0;

                                //- We reset spin count because even when we release the waiting readers,
                                //  there's no guarantee they'll actually get into the read lock, as a writer
                                //  may come right after and reserve the lock, or anything else that would normally
                                //  prevent a reader from entering.  So we may have to start the waiting process over.
                                
                                Monitor.Wait(readLockObject, MaxWaitTime);
                            }
                            
                            //- The above '((lockState & Mask.WaitingReaders)  ==  State.ReadersAreWaiting)' check
                            //  is the price of removing the Semaphores that were used in the original article.  
                            //  It prevents the case where we lose a race to a writer that happened to
                            //  exit after we decided to wait, and managed to PulseAll() before we managed to call Wait().
                            //
                            //- The check works because anyone who plans on releasing readers, has to reset the waiting
                            //  readers flag in lockState prior to calling PulseAll(), but they can't PulseAll() without
                            //  the lock we are currently holding.  So either the flag is off and we don't need to wait
                            //  because they're about to Pulse() anyways, or the waiting reader flag is still on, which
                            //  means that even if whoever has the lock is going to call PulseAll(), they haven't made
                            //  it to the part where they reset the waiting reader count yet.
                            //
                            //  Even if we managed to somehow run so slowly that they got through the entire exit process
                            //  in the time between us checking the lockState below and us calling Wait(), they still
                            //  couldn't call PulseAll() before we call Wait(), because PulseAll() can only be called
                            //  inside a lock, and we are are currently holding the lock they would need to enter. 
                            //
                            //- Note: There is another rather unlikely scenario which should be mentioned.
                            //  In the rather odd case where:
                            //      a reader has decided  to wait,
                            //      they set the lockState to indicate they are waiting,
                            //      and then in the space between setting lockState and calling this method:
                            //          the current owner of the lock exits,
                            //          another writer enters (or even less likely, the maximum number of readers enters),
                            //          another reader tries to enter,
                            //          that reader goes through the whole spinning process,
                            //          that reader decides to wait and sets the waiting reader flag,
                            
                            //  If that entire sequence were to happen, then the check below would not notice the
                            //  difference. As a result this thread would end up waiting for that new writer to exit
                            //  the lock, instead of the original one that exited before we could call Wait().
                            //   
                            
                            //- Note: Read/Write locks are intended for situations where there are significantly more readers than
                            //        writers, so the fact that waiting readers beyond the first don't have to increment a count should
                            //        cut down the chance of a CompareExchange() failing by a good margin.
                        }
                    }
                }
                //- If we're here the exchange must have failed, loop back around and try again.
            }
            
            //- Note: Read locks are kind of reentrant, but if another thread reserves the lock
            //  for writing or upgrading then re-entering could deadlock the state.
            //  We also might run into the situation where one or more threads acquires
            //  a read lock in a loop, maxes out the ActiveReaderCount, then enters
            //  again and deadlocks waiting for itself to exit. 
        }

        /// <summary>
        ///     Releases a read lock that was previously acquired on the provided lock state variable.
        /// </summary>
        /// <param name="lockState">        The variable that stores the state for the lock.                          </param>
        /// <param name="readLockObject">   The object used by the given lock state variable to synchronize readers.  </param>
        /// <param name="writeLockObject">  The object used by the given lock state variable to synchronize writers.  </param>
        public static void ExitReadLock(ref int lockState, object readLockObject, object writeLockObject)
        {
            if (readLockObject  == null) throw new ArgumentNullException(nameof(readLockObject));
            if (writeLockObject == null) throw new ArgumentNullException(nameof(writeLockObject));
            
            //- If we're not tracking that anything is in a a read lock, either the lockState is
            //  corrupted or something called this that shouldn't have.  Regardless, something is wrong.
            if ((lockState & Mask.ActiveReaderCount)  ==  State.None) { throw new SynchronizationLockException(
                $"{nameof(ExitReadLock)}() was called, but the lock state indicated it was not read locked. "); }

            int formerState;
            int newState;
            
            do
            {
                formerState = lockState;
                
#if DEBUG
                //- At this point:  Any active readers means no active writer.
                formerState.AssertWriteLockFlag_Off();
#endif
                
                //- This particular state is the only one that results in a different value.
                if (formerState == (State.OneActiveReader + State.ReadersAreWaiting))
                {
                    ReleaseReaders(ref lockState, readLockObject);
                    return;
                }
                
                newState = formerState - State.OneActiveReader;
            } 
            while (Interlocked.CompareExchange(ref lockState, newState, formerState) != formerState);

#if DEBUG
            /*- At this point:
                Active  writer flag       - should have been off, (as a condition of us reading) and should remain off.
                Reserved for upgrade flag - should have been off, (since we are the last reader) and should remain off.
                Waiting writer count      - should remain the same as it was in formerState.
                Active  reader count      - should be set to 0, we were the last reader. 
                Waiting reader flag       - should remain the same as it was in formerState.
            -*/

            newState.AssertWriteLockFlag_Off();
            newState.AssertHasSameUpgradeReservationStateAs(formerState);
            newState.HasSameWriteReservationStateAs(formerState);
            newState.HasSameNumberOfWaitingWritersAs(formerState);
            newState.AssertHasSameWaitingReadersStateAs(formerState);
            newState.AssertActiveReaderCount_OneLessThan(formerState);
#endif

            //- The only things that could trigger a reservation are an Upgrade (which can't be
            //  the case, because that can only be done by the last reader, which is us), a
            //  waiting writer, or an entering/spinning writer.
            //- If no writers are waiting, then it's just a spinning writer waiting for us to leave.
            if (((formerState & Mask.ActiveReaderCount) == State.OneActiveReader) &&
                 (formerState > (State.ReservedForWriter + State.OneWaitingWriter)))
            {
#if DEBUG
                /*- At this point:
                    Reserved for upgrade flag - should have been on, (as a condition to enter this block) and should remain on.
                    Reserved for writer flag  - should have been off, (we're the last reader) and should remain off.
                    Waiting writer count      - should have been at least one, (as a condition to enter this block).
                    Active  reader count      - should be set to 0, we were the last reader. 
                -*/
                newState.AssertReservedForWriterFlag_On();
                newState.AssertReservedForUpgradeFlag_Off();
                newState.AssertWaitingWriterCount_IsAtLeast(1);
                newState.AssertActiveReaderCount_IsZero();
#endif
                lock (writeLockObject)
                {
                    //- Note: I plan to add the ability for incoming writers to time out of trying to acquire
                    //  the lock, so we'll need to put something here in case that happens.  If the only waiting
                    //  writer timed out trying to acquire the lock then it would stay reserved, and since nothing would be there
                    //  to take the reservation, the lock would be deadlocked to readers until another writer showed up.
                    if (lockState >= (State.ReservedForWriter + State.OneWaitingWriter))
                    {
                        //- Because the waiting writer count is always modified inside a lock on the writeLockObject, and
                        //  since Monitor.Wait() can't return without acquiring the lock even if it times out, we know the
                        //  waiting writer count can't change while we hold the lock.
                        Monitor.Pulse(writeLockObject);
                    }
                }
            }
        }

        private static void ReleaseReaders(ref int lockState, object readLockObject)
        {
            int formerState;
            int newState;
            
            do
            {
                formerState = lockState;
                newState    = State.None;
            } 
            while (Interlocked.CompareExchange(ref lockState, newState, formerState) != formerState);
            
#if DEBUG
            /*- At this point:
                Active  writer flag       - should have been off, (as a condition of us reading) and should remain off.
                Reserved for upgrade flag - should have been off, (since we are the last reader) and should remain off.
                Reserved for writer flag  - should have been off, (as a condition of us calling this method) and should remain off.
                Waiting writer count      - should have been 0, (as a condition of us calling this method) and should remain 0.
                Active  reader count      - should be set to 0, we were the last reader. 
                Waiting reader flag       - should remain the same as it was in formerState.
            -*/

            newState.AssertWriteLockFlag_Off();
            newState.AssertReservedForUpgradeFlag_Off();
            newState.AssertReservedForWriterFlag_Off();
            newState.AssertWaitingWriterCount_IsZero();
            newState.AssertWaitingReadersFlag_Off();
            newState.AssertActiveReaderCount_IsZero();
#endif
            
            lock (readLockObject)
            {
                Monitor.PulseAll(readLockObject);
            }
        }


        /// <summary>
        ///     Acquires a write lock on the given lock state variable.
        /// </summary>
        /// <param name="lockState">        The variable that stores the state for the lock.                          </param>
        /// <param name="writeLockObject">  The object used by the given lock state variable to synchronize writers.  </param>
        /// <param name="crossThread">
        ///     An optional parameter for instances where the lock is used across threads, in which case the lock
        ///     avoids calling <see cref="Thread.BeginCriticalRegion "/> when entering the lock.
        /// </param>
        public static void EnterWriteLock(ref int lockState, object writeLockObject, bool crossThread = false)
        {
            if (writeLockObject == null) throw new ArgumentNullException(nameof(writeLockObject));
            
            if (crossThread == false)  { Thread.BeginCriticalRegion(); }
            
            int spinCount = 0;

            while (true)
            {
                int formerState = lockState,
                    newState;

                //- If there are no active readers or writers.
                if ((formerState & Mask.WriteLockedOrActiveReaders)  ==  State.None)
                {
#if DEBUG
                    /*- At this point:
                        The number of active readers is 0. 
                        There is no active writer.
                        The upgrade slot should not be on (it should never be on if there are no active readers)
                        A writer may have reserved the write slot.
                        The number of waiting writers may be any number from 0 to the max allowed by Mask.WaitingWriterCount.
                        There may be waiting readers.
                    -*/
                    formerState.AssertWriteLockFlag_Off();
                    formerState.AssertReservedForUpgradeFlag_Off();
                    formerState.AssertActiveReaderCount_IsZero();
#endif
                    newState = ((formerState & Mask.ExcludeWriteReservation)  |  Mask.WriteLockState);
                    
                    //- Try to claim the lock
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                    {
#if DEBUG
                        /*- If this value is set:
                            The active writer flag should now be on.
                            The reserved for upgrade flag was off, and should remain off.
                            The reserved for writer flag should now be off, regardless of it's value in formerState.
                            The waiting writer count will be the same as it was in formerState.
                            The waiting reader flag  will be the same as it was in formerState.
                            The active reader count was zero 0, and should remain 0.
                        -*/
                        newState.AssertWriteLockFlag_On();
                        newState.AssertReservedForUpgradeFlag_Off();
                        newState.AssertReservedForWriterFlag_Off();
                        newState.AssertActiveReaderCount_IsZero();
                        newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
                        newState.AssertHasSameWaitingReadersStateAs(formerState);
#endif

                        //- The exchange was successful, we have the lock.
                        return; 
                    }
                }
                //- Someone must be reading or writing.
                //  We only want a writer to reserve the lock if
                //    - It's a reader (not a writer) that's active.
                //    - The reader has not decided to upgrade.
                //    - The lock has not already been reserved by a writer.
                //  Because the WriteLockState and ReservedForUpgrade flags are higher than
                //  the ReservedForWriter flag, the check below covers all 3 cases.
                else if (formerState < State.ReservedForWriter)
                {
#if DEBUG
                    /*- At this point:
                        The Mask.WriteLockState is off, so there should be no active writer.
                        The State.ReservedForWriter flag is off.
                        The number of active readers is > 0, otherwise the check above this would've been true. 
                        The number of waiting writers may be any number from 0 to the max allowed.
                        There may be waiting readers.
                    -*/
                    formerState.AssertActiveReaderCount_IsAtLeast(1);
                    formerState.AssertWriteLockFlag_Off();
                    formerState.AssertReservedForUpgradeFlag_Off();
                    formerState.AssertReservedForWriterFlag_Off();
#endif
                    
                    //- Spinning/Waiting writers only reserve the lock during a read lock.
                    //  This means that if a reader and a writer are spinning waiting for the active
                    //  writer to exit, once the active writer does exit, the lock will go to whoever
                    //  wakes up from the spin and successfully exchanges the lockState first. The
                    //  writer is guaranteed to get into the lock in a short amount of time either way.
                    //  Once it reaches its spin limit it will mark itself as a 'waiting writer' and both
                    //  exit methods automatically give preference to waiting writers, as well as activating
                    //  the reserved for writer flag when they wake up the waiting writer.

                    newState = (formerState | State.ReservedForWriter);
                    //- Try to reserve the lock.
                    Interlocked.CompareExchange(ref lockState, newState, formerState);
#if DEBUG
                    /*- If this value is set:
                        The Mask.WriteLockState unchanged, but should still be off.
                        The State.ReservedForWriter bit now ensured to be on.
                        The active reader count is unchanged, but should be > 0, as mentioned before.
                        The waiting writer count will be the same as it was in formerState.
                        The waiting reader count will be the same as it was in formerState.
                    -*/
                    
                    newState.AssertWriteLockFlag_Off();
                    newState.AssertReservedForUpgradeFlag_Off();
                    newState.AssertReservedForWriterFlag_On();
                    newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
                    newState.AssertHasSameWaitingReadersStateAs(formerState);
                    newState.AssertHasSameNumberOfActiveReadersAs(formerState);
#endif
                    //-  We'll be going back through the loop to do the spinning/waiting process.
                }
                //- Some writer owns or has reserved the lock (maybe us), so now we wait.  
                //  If we haven't done much spinning yet...
                else if (spinCount < Number_Of_Spins)
                {
#if DEBUG
                    /*- At this point:
                        A writer may be active.
                        If writer is not active, either/both of the 'reserved for writer' and 
                        'reserved for upgrade' flags is on, and there is at least one active reader.
                        There may be waiting readers.
                    -*/
                    formerState.AssertReservedOrWriterActive();
#endif
                    //- Spin to win
                    Threading.Spin(spinCount++);
                }
                //- Well we've tried spinning and still nothing.
                //  If there are waiting writer slots left...
                else if ((formerState & Mask.WaitingWriterCount)  <  State.MaxWaitingWriters) 
                {
#if DEBUG
                    //- At this point: Same rules apply as the spinning section,
                    //  except that the number of waiting writers is less than the
                    //  maximum allowed.

                    formerState.AssertReservedOrWriterActive();
                    formerState.AssertWaitingWriterCount_NotAtMax();
#endif
                    //- Since there is a wait slot available, if we can claim it...
                    lock (writeLockObject)
                    {
                        //- An active reader/writer will disable their lock flag as part of exiting,
                        //  so we check for that to see if they exited before we got to call Monitor.Wait().
                        //  Even if they exit and someone else enters before we wait, either they'll stay in
                        //  their read/write lock long enough for us to increment the wait count, or they won't
                        //  and the check below will cause us to skip the wait.
                        //  We don't have to worry about an exiting thread calling Pulse() in the time between
                        //  when we set the waiting writer count, and when we call Wait(), because Pulse() can't
                        //  be called without the lock that we're holding.
                        while (((formerState = lockState) & Mask.WriteLockedOrActiveReaders)  !=  State.None)
                        {
                            newState = (formerState + State.OneWaitingWriter);

                            if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                            {
#if DEBUG
                                //- If this value is set: The only change should be that the waiting
                                //  writer count is 1 higher than it was in formerState.
                                newState.AssertHasSameWriteLockStateAs(formerState);
                                newState.AssertHasSameUpgradeReservationStateAs(formerState);
                                newState.AssertHasSameWriteReservationStateAs(formerState);
                                newState.AssertHasOneMoreWaitingWriterThan(formerState);
                                newState.AssertHasSameWaitingReadersStateAs(formerState);
                                newState.AssertHasSameNumberOfActiveReadersAs(formerState);
#endif
                                //- We'll wait for whoever is using the lock to wake us up.
                                Monitor.Wait(writeLockObject, MaxWaitTime);

                                //- TODO : If we time out, we should probably try to acquire the lock one last time if we're the last waiting writer.
                                //         That way if the lock is reserved we won't leave it in that state by exiting.
                                do
                                {
                                    formerState = lockState;
                                    newState    = (formerState - State.OneWaitingWriter);
                                } 
                                while (Interlocked.CompareExchange(ref lockState, newState, formerState)  !=  formerState);
#if DEBUG
                                /*- If this value is set:
                                        formerState should reflect there was at least waiting writer, otherwise something else changed it.
                                        Waiting writer count should be set to 1 less than it was in formerState.
                                -*/
                                formerState.AssertWaitingWriterCount_IsAtLeast(1);
                                newState.AssertHasSameWriteLockStateAs(formerState);
                                newState.AssertHasSameUpgradeReservationStateAs(formerState);
                                newState.AssertHasSameWriteReservationStateAs(formerState);
                                newState.AssertHasOneFewerWaitingWritersThan(formerState);
                                newState.AssertHasSameNumberOfActiveReadersAs(formerState);
                                newState.AssertHasSameWaitingReadersStateAs(formerState);
#endif
                                spinCount = 0; 
                                //- The waiting section of EnterReadLock() has an explanation for
                                //  why we reset this to 0.

                                break; 
                            }
                        }
                    }
                }
                //- We wanted to wait but there aren't any slots left...
                else 
                {
#if DEBUG
                    //- At this point: Same rules apply as the spinning section,
                    //  except that the number of waiting writers is at the
                    //  maximum allowed.
                    formerState.AssertReservedOrWriterActive();
                    formerState.AssertWaitingWriterCount_AtMax();
#endif

                    //- It's going to be a while, so let's go to sleep while we wait for a slot
                    Thread.Sleep(Long_Sleep_Duration);
                }
            }
        }

        /// <summary>
        ///     Releases a write lock that was previously acquired on the provided lock state variable.
        /// </summary>
        /// <param name="lockState">        The variable that stores the state for the lock.                          </param>
        /// <param name="readLockObject">   The object used by the given lock state variable to synchronize readers.  </param>
        /// <param name="writeLockObject">  The object used by the given lock state variable to synchronize writers.  </param>
        /// <param name="crossThread">
        ///     An optional parameter for instances where the lock is used across threads, in which case the lock
        ///     avoids calling <see cref="Thread.EndCriticalRegion"/> when releasing the lock.
        /// </param>
        public static void ExitWriteLock(ref int lockState, object readLockObject, object writeLockObject, bool crossThread = false)
        {
            _ = writeLockObject ?? throw new ArgumentNullException(nameof(writeLockObject));
            _ =  readLockObject ?? throw new ArgumentNullException(nameof(readLockObject));
            
            if (lockState < State.WriteLockActive)
            {
                //  Something may have called this method that shouldn't have, or the state got corrupted, 
                //  who knows, but if we continue our lockState will be invalid.
                throw new SynchronizationLockException(
                $"{nameof(ExitWriteLock)}() was called,  was called, but the lock state indicated it was not write locked. ");
            }
            
            //- TODO : Consider moving the check above into the loop below to ensure we throw an
            //  exception if we get into the loop and the user calls this method simultaneously
            //  on two different threads and the both get past the above check,
            //  or does something else that disables the active writer flag while
            //  we are looping. If we do, we should do the same for ExitReadLock().
            
            int formerState;
            int newState;

            do
            {
                formerState = lockState;
#if DEBUG
                /*- At this point:
                    The active writer flag is on.
                    Nothing should reserve the lock during a write lock. 
                    The number of active readers should be 0, since there is an active writer. 
                -*/
                formerState.AssertWriteLockFlag_On();
                formerState.AssertReservedForUpgradeFlag_Off();
                formerState.AssertReservedForWriterFlag_Off();
                formerState.AssertActiveReaderCount_IsZero();
#endif
                //  None of the 'Reserved for Upgrade', 'Reserved For Writer', or 'Active Reader Count' flags
                //  can be active during a write lock, so all we have to check is whether there's a writer
                //  waiting.  
                if (formerState < (State.WriteLockActive + State.OneWaitingWriter))
                {
                    //- There's no waiting writers.
#if DEBUG
                    formerState.AssertWaitingWriterCount_IsZero();
#endif
                    
                    newState = State.None; 
                    
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) ==  formerState)
                    {
#if DEBUG
                        newState.AssertWriteLockFlag_Off();
                        newState.AssertReservedForUpgradeFlag_Off();
                        newState.AssertReservedForWriterFlag_Off();
                        newState.AssertWaitingWriterCount_IsZero();
                        newState.AssertWaitingReadersFlag_Off();
                        newState.AssertActiveReaderCount_IsZero();
#endif
                        if (formerState  != State.WriteLockActive)
                        {
                            //- The only flag low enough to be active and still allow  us to pass both
                            //  checks to get here is the waiting readers flag, so release them.
#if DEBUG
                            formerState.AssertWaitingReadersFlag_On();
#endif
                            
                            lock (readLockObject)
                            {
                                Monitor.PulseAll(readLockObject);
                            }
                        }
                        
                        break;
                    }
                }
                //- There are waiting writers, release one.
                else
                {
#if DEBUG
                    formerState.AssertWaitingWriterCount_IsAtLeast(1);
#endif
                    
                    lock (writeLockObject)
                    {
                        //- We have to check again, because if the only writer timed out of Monitor.Wait()
                        //  then  we would reserve it thinking there was a waiting writer.  Since nothing
                        //  would be there to take the reservation, the lock would be deadlocked to readers
                        //  until another writer showed up.
                        
                        //- Because the waiting writer count is always modified inside a lock on the writeLockObject, and
                        //  since Monitor.Wait() can't return without acquiring the lock even if it times out, if we fail
                        //  the exchange there's no need to go back through the outer loop.  Since we know the waiting writer
                        //  count can't change while we hold the lock, we can just keep attempting to exchange the lockState
                        //  until it succeeds.   
                        formerState = lockState;
                        newState    = formerState + (State.ReservedForWriter - State.WriteLockActive);

                        if (formerState >= (State.WriteLockActive + State.OneWaitingWriter))
                        {
                            while (Interlocked.CompareExchange(ref lockState, newState,formerState)  !=  formerState)
                            {
                                formerState = lockState;
                                newState    = formerState + (State.ReservedForWriter - State.WriteLockActive);
                            }
#if DEBUG
                            newState.AssertWriteLockFlag_Off();
                            newState.AssertReservedForUpgradeFlag_Off();
                            newState.AssertReservedForWriterFlag_On();
                            newState.HasSameNumberOfWaitingWritersAs(formerState);
                            newState.AssertHasSameWaitingReadersStateAs(formerState);
                            newState.AssertActiveReaderCount_IsZero();
#endif
                            Monitor.Pulse(writeLockObject);
                            
                            break;
                        }
                    }
                }
            }
            while (true);

            if (crossThread == false) { Thread.EndCriticalRegion(); }
        }



        //- I've designed Upgrade() to take priority over standard writers because if someone is trying to
        //  upgrade, chances are that whatever they want to do is based on the value they've seen in the
        //  read lock.  
        //  If another writer gets access before the reader wanting to upgrade, they may completely change the state
        //  to the point where upgrading was a waste of time.  A regular writer has should have no knowledge
        //  of the state the lock protects until they enter, so they have less to lose if someone gets there
        //  first.
        
        /// <summary>
        ///     Attempts to upgrades a read lock, that was previously acquired on the provided
        ///     lock state variable, to a write lock.
        /// </summary>
        /// <param name="lockState">        The variable that stores the state for the lock.                          </param>
        /// <param name="writeLockObject">  The object used by the given lock state variable to synchronize writers.  </param>
        /// <param name="crossThread">
        ///     An optional parameter for instances where the lock is used across threads, in which case the lock
        ///     avoids calling <see cref="Thread.BeginCriticalRegion "/> when entering the write lock.
        /// </param>
        /// <returns>  A bool indicating whether the upgrade was successful.  </returns>
        /// <remarks>
        ///     <para>
        ///     Since upgrading puts you into a write lock, you must call <see cref="ExitWriteLock"/> 
        ///     instead of <see cref="ExitReadLock"/> when you are finished using the lock.
        ///     </para>
        ///     <para>
        ///     An upgrading reader will have priority over any waiting/incoming writers.
        ///     Only one reader is allowed to upgrade at a time.  If two or more readers
        ///     attempt to upgrade during the same read lock, the ones who came after the
        ///     first will be converted to regular writers, and the method will return false
        ///     to indicate the anything seen during the read lock is no longer guaranteed.
        ///     </para>
        /// </remarks>
        public static bool Upgrade(ref int lockState, object writeLockObject, bool crossThread = false)
        {
            if (writeLockObject == null) throw new ArgumentNullException(nameof(writeLockObject));

            int formerState;
            int newState;

            //- Invariant: the reader never gives up its active reader slot until it upgrades,
            //  or it finds out the lock is already reserved

            while ((formerState = lockState) < State.ReservedForUpgrade)
            {
                //- In order to upgrade the lock, we need to be the only reader left in it.
                if ((formerState & Mask.ActiveReaderCount) == State.OneActiveReader)
                {
#if DEBUG
                    formerState.AssertWriteLockFlag_Off();
                    //- If we're the last reader we just convert straight into a write lock,
                    //  A reader only reserves the lock if it wasn't originally the last one.
                    //  The code that reserves the lock shouldn't loop again, so there should be
                    //  no way for a reader to be here if it has that flag.
                    formerState.AssertReservedForUpgradeFlag_Off();
                    formerState.AssertActiveReaderCount_IsOne();
#endif
                    //- Add the write lock .
                    newState = (formerState & Mask.ExcludeReservationsAndActiveReaderCount) | State.WriteLockActive;

                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) == formerState)
                    {
#if DEBUG
                        newState.AssertWriteLockFlag_On();
                        newState.AssertReservedForUpgradeFlag_Off();
                        newState.AssertReservedForWriterFlag_Off();
                        newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
                        newState.AssertHasSameWaitingReadersStateAs(formerState);
                        newState.AssertActiveReaderCount_IsZero();
#endif

                        if (crossThread == false) { Thread.BeginCriticalRegion(); }

                        return true;
                    }
                }
                else if ((formerState & Mask.ActiveReaderCount) == State.None)
                {
                    throw new SynchronizationLockException(
                        $"A process called {nameof(Upgrade)}, but no threads were tracked as being in a reading state. ");
                }
                else if (formerState < State.ReservedForUpgrade)
                {
#if DEBUG
                    formerState.AssertWriteLockFlag_Off();
                    formerState.AssertReservedForUpgradeFlag_Off();
                    formerState.AssertActiveReaderCount_IsAtLeast(2);
#endif
                    //- Apparently no one has reserved the upgrade slot yet, so we'll try to claim it.
                    //- Even if a writer had the write reservation, we have priority, so they'll have to wait.
                    newState = (formerState | (State.ReservedForUpgrade | State.ReservedForWriter));

                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) == formerState)
                    {
#if DEBUG
                        newState.AssertWriteLockFlag_Off();
                        newState.AssertReservedForUpgradeFlag_On();
                        newState.AssertReservedForWriterFlag_On();
                        newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
                        newState.AssertHasSameWaitingReadersStateAs(formerState);
                        newState.AssertHasSameNumberOfActiveReadersAs(formerState);
#endif
                        while (((formerState = lockState) & Mask.ActiveReaderCount) > State.OneActiveReader)
                        {
                            //- We're just waiting for the rest of the readers to finish up.
                            Threading.Spin(1);
                        }

                        //- Add the writer flag, remove both reservation flags, and set the reader count to 0.
                        newState = formerState ^ (State.WriteLockActive | State.ReservedForUpgrade | State.ReservedForWriter | State.OneActiveReader);

                        while (Interlocked.CompareExchange(ref lockState, newState, formerState) != formerState)
                        {
                            formerState = lockState;
                            newState = formerState ^
                                       (State.WriteLockActive | State.ReservedForUpgrade | State.ReservedForWriter | State.OneActiveReader);
                        }

#if DEBUG
                        newState.AssertWriteLockFlag_On();
                        newState.AssertReservedForUpgradeFlag_Off();
                        newState.AssertReservedForWriterFlag_Off();
                        newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
                        newState.AssertHasSameWaitingReadersStateAs(formerState);
                        newState.AssertActiveReaderCount_IsZero();
#endif

                        if (crossThread == false) { Thread.BeginCriticalRegion(); }

                        return true;
                    }
                }
            }
            //- It's already reserved.
#if DEBUG
                formerState.AssertWriteLockFlag_Off();
                formerState.AssertReservedForUpgradeFlag_On();
                formerState.AssertReservedForWriterFlag_On();
                //- Someone reserved it, so another reader must exist
                formerState.AssertActiveReaderCount_IsAtLeast(2); 
#endif
                
                //- There is no process that would cause the reserved for upgrade flag
                //  to change while we're in the read lock, so there's no chance we're 
                //  going to get the upgrade slot.  All we can do is remove ourselves
                //  from the read lock and enter as a normal writer (two readers waiting
                //  to upgrade would deadlock because neither would leave).
                newState = formerState - State.OneActiveReader;
                
                //- The fact that it's read locked and reserved means the only flags left that might
                //  change are waiting readers and writers, neither of those changes the fact that
                //  we need to get out of the lock, so if we fail the exchange just keep trying.
                while (Interlocked.CompareExchange(ref lockState, newState, formerState) != formerState)
                {
                    formerState = lockState;
                    newState    = formerState - State.OneActiveReader;
                }
                
                EnterWriteLock(ref lockState, writeLockObject, crossThread);

                return false;
                
                //- TODO : Since we changed the structure of the lock we could revisit the
                //  idea of having multiple queued upgrades.  We could use the 9 bits we
                //  got back after compressing the waiting reader flag to make an upgrade 
                //  count.  Or since we have a dedicated upgrade flag now, we could have the 
                //  other upgraders follow a similar pattern as EnterWriteLock().
                //  where they activate the reserved for upgrade flag and spin for a bit.
                //  The only real benefit that would have over converting them to writers
                //  would be if we wanted those upgraders to retain priority over any
                //  present/incoming writers.  Even then though, once the first reader upgrades
                //  all the others have lost the guarantee that they know anything about the
                //  state of whatever the lock protects.
                
                //- Also, consider if having a flag to differentiate an upgraded reader
                //  that is writing, from a regular active writer, would be useful.
        }
        
        /// <summary>
        ///     Downgrades a write lock, that was previously acquired on the provided
        ///     lock state variable, to a read lock.
        /// </summary>
        /// <param name="lockState">        The variable that stores the state for the lock.                         </param>
        /// <param name="readLockObject">  The object used by the given lock state variable to synchronize readers.  </param>
        /// <param name="crossThread">
        ///     An optional parameter for instances where the lock is used across threads, in which case the lock
        ///     avoids calling <see cref="Thread.EndCriticalRegion"/> when releasing the write lock.
        /// </param>
        /// <remarks> 
        ///     Since downgrading puts you into a read lock, you must call <see cref="ExitReadLock"/> 
        ///     instead of <see cref="ExitWriteLock"/> when you are finished using the lock.
        /// </remarks>
        public static void Downgrade(ref int lockState, object readLockObject, bool crossThread = false)
        {
            if (readLockObject  == null) throw new ArgumentNullException(nameof(readLockObject));
            
            int formerState;
            int newState;
            
            if (lockState < State.WriteLockActive)
            {
                //  Something may have called this method that shouldn't have, or the state got corrupted, 
                //  who knows, but if we continue the lock state will be invalid.
                throw new SynchronizationLockException(
                    $"{nameof(ExitWriteLock)}() was called, but the object was not write locked. ");
            }
            
            //- Downgrading is pretty simple because if we're in a write lock
            //  there can't be any other readers or writers active.
            
            do
            {
                formerState = lockState;
                
#if DEBUG
                formerState.AssertWriteLockFlag_On();
                formerState.AssertReservedForUpgradeFlag_Off();
                formerState.AssertReservedForWriterFlag_Off();
                formerState.AssertActiveReaderCount_IsZero();
#endif
                
                //- If the only other flag is that readers are waiting, release them so they can enter the read lock too.
                if (formerState  ==  (State.WriteLockActive + State.ReadersAreWaiting))
                {
#if DEBUG
                    formerState.AssertWaitingWriterCount_IsZero();
#endif
                    newState = formerState - (State.WriteLockActive + State.ReadersAreWaiting - State.OneActiveReader);
                    
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                    {
#if DEBUG
                        newState.AssertWaitingReadersFlag_Off();
#endif
                        lock (readLockObject)
                        {
                            Monitor.PulseAll(readLockObject);
                        }
                        
                        break;
                    }
                }
                else 
                {
                    newState = formerState - (State.WriteLockActive - State.OneActiveReader);

                    if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                    {
                        break;
                    }
                }
            }
            while (true);
            
#if DEBUG
            newState.AssertWriteLockFlag_Off();
            newState.AssertReservedForUpgradeFlag_Off();
            newState.AssertReservedForWriterFlag_Off();
            newState.AssertHasSameNumberOfWaitingWritersAs(formerState);
            newState.AssertActiveReaderCount_IsOne();
#endif

            if (crossThread == false) { Thread.EndCriticalRegion(); }
        }
        
        #endregion
    }
}