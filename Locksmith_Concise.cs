using System;
 using System.Diagnostics;
 using System.Threading;


namespace Dextarius
{
    //- A copy of the code from the Locksmith class that has all of the debugging
    //  and extraneous comments removed, for people who just want to read the code.
    internal static class Locksmith_Concise
    {
        #region Constants

        private const int Long_Sleep_Duration = 10;
        private const int Number_Of_Spins     = 15;
        
        private static class Mask
        {
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
                OneActiveReader    = (Mask.WaitingReaders - MaxActiveReaders), 
                OneWaitingWriter   = (Mask.WaitingReaders + Mask.ActiveReaderCount + 1);
        }

        #endregion

        #region Fields

        private static int maxWaitTime = 10_000;

        #endregion
        

        #region Properties

        public static int MaxNumberOfWaitingWriters => State.MaxWaitingWriters / State.OneWaitingWriter;
        public static int MaxNumberOfActiveReaders  => State.MaxActiveReaders;


        //- Controls the maximum amount of time a thread will wait during Monitor.Wait()
        internal static int MaxWaitTime
        {
            get => maxWaitTime;
            set => Interlocked.Exchange(ref maxWaitTime, value);
        }

        #endregion
        
        
        #region Methods
        
        //- Acquires a read lock.
        public static void EnterReadLock(ref int lockState, object readLockObject)
        {
            if (readLockObject == null) throw new ArgumentNullException(nameof(readLockObject));

            int spinCount = 0;

            while (true)
            {
                int formerState = lockState;
                int newState;
                
                if (formerState < State.MaxActiveReaders) 
                {
                    newState = formerState + State.OneActiveReader;
                    
                    //- If we can claim the reader slot
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) == formerState)
                    {
                        return; //- We've entered the read lock.
                    }
                    
                    Threading.BriefWait();
                }
                //- If we haven't spun much yet..
                else if (spinCount < Number_Of_Spins)
                {
                    Threading.Spin(spinCount++);
                }
                //- We've spun a bunch, so it's time to wait...
                else
                {
                    newState = (formerState | State.ReadersAreWaiting);
                    
                    if ((formerState & Mask.WaitingReaders)  ==  State.ReadersAreWaiting ||
                        Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState) 
                    {
                        lock (readLockObject)
                        {
                            if ((lockState & Mask.WaitingReaders)  ==  State.ReadersAreWaiting)
                            {
                                spinCount = 0;

                                Monitor.Wait(readLockObject, MaxWaitTime);
                            }
                        }
                    }
                }
            }
        }

        //- Releases a read lock that was previously acquired on the provided lock state variable.
        public static void ExitReadLock(ref int lockState, object readLockObject, object writeLockObject)
        {
            if (readLockObject  == null) throw new ArgumentNullException(nameof(readLockObject));
            if (writeLockObject == null) throw new ArgumentNullException(nameof(writeLockObject));
            if ((lockState & Mask.ActiveReaderCount)  ==  State.None) { throw new SynchronizationLockException(
                $"{nameof(ExitReadLock)}() was called, but the lock state indicated it was not read locked. "); }

            int formerState;
            int newState;
            
            do
            {
                formerState = lockState;
                
                //- This particular state is the only one that results in a different value.
                if (formerState == (State.OneActiveReader + State.ReadersAreWaiting))
                {
                    ReleaseReaders(ref lockState, readLockObject);
                    return;
                }
                
                newState = formerState - State.OneActiveReader;
            } 
            while (Interlocked.CompareExchange(ref lockState, newState, formerState) != formerState);
            
            if (((formerState & Mask.ActiveReaderCount) == State.OneActiveReader) &&
                 (formerState > (State.ReservedForWriter + State.OneWaitingWriter)))
            {
                lock (writeLockObject)
                {
                    if (lockState >= (State.ReservedForWriter + State.OneWaitingWriter))
                    {
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

            lock (readLockObject)
            {
                Monitor.PulseAll(readLockObject);
            }
        }

        
        //- Acquires a write lock.
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
                    newState = ((formerState & Mask.ExcludeWriteReservation)  |  Mask.WriteLockState);
                    
                    //- Try to claim the lock
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                    {
                        //- The exchange was successful, we have the lock.
                        return; 
                    }
                }
                else if (formerState < State.ReservedForWriter)
                {
                    newState = (formerState | State.ReservedForWriter);
                    //- Try to reserve the lock.
                    Interlocked.CompareExchange(ref lockState, newState, formerState);

                    //-  We'll be going back through the loop to do the spinning/waiting process.
                }
                //- Some writer owns or has reserved the lock (maybe us), so now we wait.  
                //  If we haven't done much spinning yet...
                else if (spinCount < Number_Of_Spins)
                {
                    Threading.Spin(spinCount++);
                }
                //- Well we've tried spinning and still nothing.
                //  If there are waiting writer slots left...
                else if ((formerState & Mask.WaitingWriterCount)  <  State.MaxWaitingWriters) 
                {
                    //- Since there is a wait slot available, if we can claim it...
                    lock (writeLockObject)
                    {
                        while (((formerState = lockState) & Mask.WriteLockedOrActiveReaders)  !=  State.None)
                        {
                            newState = (formerState + State.OneWaitingWriter);

                            if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                            {
                                //- We'll wait for whoever is using the lock to wake us up.
                                Monitor.Wait(writeLockObject, MaxWaitTime);
                                
                                do
                                {
                                    formerState = lockState;
                                    newState    = (formerState - State.OneWaitingWriter);
                                } 
                                while (Interlocked.CompareExchange(ref lockState, newState, formerState)  !=  formerState);

                                spinCount = 0;

                                break;
                            }
                        }
                    }
                }
                //- We wanted to wait but there aren't any slots left...
                else 
                {
                    //- It's going to be a while, so let's go to sleep while we wait for a slot
                    Thread.Sleep(Long_Sleep_Duration);
                }
            }
        }
        
        public static void ExitWriteLock(ref int lockState, object readLockObject, object writeLockObject, bool crossThread = false)
        {
            _ = writeLockObject ?? throw new ArgumentNullException(nameof(writeLockObject));
            _ =  readLockObject ?? throw new ArgumentNullException(nameof(readLockObject));
            
            if (lockState < State.WriteLockActive) { throw new SynchronizationLockException(
                $"{nameof(ExitWriteLock)}() was called,  was called, but the lock state indicated it was not write locked. "); }

            int formerState;
            int newState;

            do
            {
                formerState = lockState;
                
                //  None of the 'Reserved for Upgrade', 'Reserved For Writer', or 'Active Reader Count'
                //  flags can be active during a write lock, so all we have to check is whether there's
                //  a writer waiting.  
                if (formerState < (State.WriteLockActive + State.OneWaitingWriter))
                {
                    //- There's no waiting writers.
                    newState = State.None; 
                    
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) ==  formerState)
                    {
                        if (formerState  != State.WriteLockActive)
                        {
                            //- The only flag low enough to be active and still allow  us to pass both
                            //  checks to get here is the waiting readers flag, so release them.
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
                    lock (writeLockObject)
                    {
                        formerState = lockState;
                        newState    = formerState + (State.ReservedForWriter - State.WriteLockActive);

                        if (formerState >= (State.WriteLockActive + State.OneWaitingWriter))
                        {
                            while (Interlocked.CompareExchange(ref lockState, newState,formerState)  !=  formerState)
                            {
                                formerState = lockState;
                                newState    = formerState + (State.ReservedForWriter - State.WriteLockActive);
                            }
                            
                            Monitor.Pulse(writeLockObject);
                            
                            break;
                        }
                    }
                }
            }
            while (true);

            if (crossThread == false) { Thread.EndCriticalRegion(); }
        }
        
        public static bool Upgrade(ref int lockState, object writeLockObject, bool crossThread = false)
        {
            if (writeLockObject == null) throw new ArgumentNullException(nameof(writeLockObject));

            int formerState;
            int newState;

            while ((formerState = lockState) < State.ReservedForUpgrade)
            {
                //- In order to upgrade the lock, we need to be the only reader left in it.
                if ((formerState & Mask.ActiveReaderCount) == State.OneActiveReader)
                {
                    //- Add the write lock.
                    newState = (formerState & Mask.ExcludeReservationsAndActiveReaderCount) | State.WriteLockActive;

                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) == formerState)
                    {
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
                    //- Apparently no one has reserved the upgrade slot yet, so we'll try to claim it.
                    newState = (formerState | (State.ReservedForUpgrade | State.ReservedForWriter));

                    if (Interlocked.CompareExchange(ref lockState, newState, formerState) == formerState)
                    {
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

                        if (crossThread == false) { Thread.BeginCriticalRegion(); }

                        return true;
                    }
                }
            }
            //- It's already reserved.
            
            newState = formerState - State.OneActiveReader;
            
            while (Interlocked.CompareExchange(ref lockState, newState, formerState) != formerState)
            {
                formerState = lockState;
                newState    = formerState - State.OneActiveReader;
            }
            
            EnterWriteLock(ref lockState, writeLockObject, crossThread);

            return false;
        }
        
        //- Downgrades a write lock to a read lock.
        public static void Downgrade(ref int lockState, object readLockObject, bool crossThread = false)
        {
            if (readLockObject  == null) throw new ArgumentNullException(nameof(readLockObject));
            
            int formerState;
            int newState;
            
            if (lockState < State.WriteLockActive) { throw new SynchronizationLockException(
                    $"{nameof(ExitWriteLock)}() was called, but the object was not write locked. "); }
            
            //- Downgrading is pretty simple because if we're in a write lock
            //  there can't be any other readers or writers active.
            do
            {
                formerState = lockState;

                //- If the only other flag is that readers are waiting, release them so they can enter the read lock too.
                if (formerState  ==  (State.WriteLockActive + State.ReadersAreWaiting))
                {
                    newState = formerState - (State.WriteLockActive + State.ReadersAreWaiting - State.OneActiveReader);
                    
                    if (Interlocked.CompareExchange(ref lockState, newState, formerState)  ==  formerState)
                    {
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

            if (crossThread == false) { Thread.EndCriticalRegion(); }
        }
        
        #endregion
    }
}