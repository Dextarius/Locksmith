using System;
using System.Threading;
using Dextarius;
using NUnit.Framework;


namespace Tests
{
    //- Note : There's a lot of opportunity to reduce redundant code,
    //         especially the local methods used by the tests.
    public class LocksmithTests
    {
        private const int ReaderTests = 0;
        private const int WriterTests = 10_000;
        
        //- Storing the lock objects the tests use in global variables may not be wise,
        //  but it does test it more thoroughly, since the methods should be always 
        //  leave the lock in a valid state.
        public static object readLockTestObject   = new object();  
        public static object writeLockTestObject  = new object();  
        public static int    lockState            = 0;
        
        //- These are mostly just here to make interpreting the state of the lock easier.
        //  I can check them in the debugger instead of having to count bits in my head.
        public bool WriteLockActive        => Locksmith.IsWriteLocked(lockState);
        public bool ReservedForUpgrade     => Locksmith.IsReservedForUpgrade(lockState);
        public bool ReservedForWriter      => Locksmith.IsReservedForWriter(lockState);
        public int  NumberOfWaitingWriters => Locksmith.NumberOfWaitingWriters(lockState);
        public bool WaitingWritersIsAtMax  => lockState.IsAtMaxWaitingWriters();
        public int  NumberOfActiveReaders  => Locksmith.NumberOfActiveReaders(lockState);
        public bool ActiveReadersIsAtMax   => Locksmith.IsAtMaxActiveReaders(lockState);
        public bool WritersAreWaiting      => lockState.HasWaitingWriters();
        public bool ReadersAreWaiting      => lockState.HasWaitingReaders();
        public bool IsReadLocked           => Locksmith.IsReadLocked(lockState);
        
        [SetUp]
        public void Setup()
        {
        }
        
        [Test, Order(1), Timeout(2000)]
        public void Locksmith_WhileUnlocked_AllowsAReaderToEnter()
        {
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            {
                Thread.SpinWait(1);
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
        
        [Test, Order(2), Timeout(2000)]
        public void Locksmith_WhileUnlocked_AllowsAWriterToEnter()
        {
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                Thread.SpinWait(1);
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
        
        
        //- This test should not be ran with the current bit values used by the Locksmith class
        //  I believe it's rather unlikely you want to try to start a quarter million threads 
        //  I usually run this test with the max reader count set to 2047.
        //  I'll have to make a test that creates a fake lock state so that we can test the new max.
        //[Test, Order(3)]
        public void Locksmith_WhileNotWriteLocked_AllowsMaxConcurrentReadersToEnter()
        {
            int              numberOfThreadsToCreate = Locksmith.MaxNumberOfActiveReaders;
            int              numberOfReads           = 0;
            CountdownEvent   readersEnteredEvent     = new CountdownEvent(numberOfThreadsToCreate);
            ManualResetEvent lockTestedEvent         = new ManualResetEvent(false);
            
            
            for (int i = 0; i < numberOfThreadsToCreate; i++)
            {
                StartNewThreadThatRuns(IncrementNumReadsInsideReadLock);
            }

            readersEnteredEvent.Wait();
            Assert.That(numberOfReads, Is.EqualTo(numberOfThreadsToCreate));
            Assert.That(lockState.NumberOfActiveReaders(), Is.EqualTo(Locksmith.MaxNumberOfActiveReaders));
            lockTestedEvent.Set();
            
            return;
            
            
            void IncrementNumReadsInsideReadLock()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    Interlocked.Increment(ref numberOfReads);
                    readersEnteredEvent.Signal();
                    lockTestedEvent.WaitOne();
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }
        
        //- This test should not be ran with the current bit values used by the Locksmith class
        //  I believe it's rather unlikely you want to try to start a quarter million threads 
        //  I usually run this test with the max reader count set to 2047.
        //  I'll have to make a test that creates a fake lock state so that we can test the new max.
        //[Test, Order(4), ]
        public void Locksmith_WhenReadLockedAndAtMaxActiveReaders_CausesNewReadersToWait()
        {
            //- Since we need all of the reader slots in order to run this test, we'll use a separate 
            //  set of objects than the rest.
            int     methodsLockState = 0;  
            object  methodsReadLockObject  = new object();  
            object  methodsWriteLockObject = new object();  

            int     numberOfThreadsToCreate     = Locksmith.MaxNumberOfActiveReaders - 1;
            int     numberOfIncrementingThreads = numberOfThreadsToCreate + 1;
            int     numberOfReads               = 0;
            Barrier barrierToKeepReadersInLock = new Barrier(numberOfIncrementingThreads); 
            
            Locksmith.EnterReadLock(ref methodsLockState, methodsReadLockObject);
            {
                for (int i = 0; i < numberOfThreadsToCreate; i++)
                {
                    StartNewThreadThatRuns(IncrementNumReadsInsideReadLock);
                }
                
                Thread.Sleep(200);
                Assert.AreEqual(numberOfReads, numberOfThreadsToCreate); 
                Assert.AreEqual(Locksmith.NumberOfActiveReaders(methodsLockState), Locksmith.MaxNumberOfActiveReaders);
                
                Thread setValueThread = new Thread(IncrementNumReadsInsideReadLock) { IsBackground = false };
                setValueThread.Start();

                Thread.Sleep(200);

                while (true)
                {
                    if ((setValueThread.ThreadState & ThreadState.WaitSleepJoin) == ThreadState.WaitSleepJoin)
                    {
                        Assert.AreEqual(Locksmith.NumberOfActiveReaders(methodsLockState), Locksmith.MaxNumberOfActiveReaders);
                        Assert.That(methodsLockState.HasWaitingReaders(), $"{methodsLockState}");
                        break;
                    }
                }
                
                barrierToKeepReadersInLock.SignalAndWait();
            }
            Locksmith.ExitReadLock(ref methodsLockState, methodsReadLockObject, methodsWriteLockObject);
            
            return;
            
            
            void IncrementNumReadsInsideReadLock()
            {
                Locksmith.EnterReadLock(ref methodsLockState, methodsReadLockObject);
                {
                    Interlocked.Increment(ref numberOfReads);
                    barrierToKeepReadersInLock.SignalAndWait();
                }
                Locksmith.ExitReadLock(ref methodsLockState, methodsReadLockObject, methodsWriteLockObject);
            }
        }

        //- This test should not be ran with the current bit values used by the Locksmith class
        //  I believe it's rather unlikely you want to try to start a quarter million threads 
        //  I usually run this test with the max reader count set to 2047.
        //  I'll have to make a test that creates a fake lock state so that we can test the new max.
        //[Test, Order(5)]
        public void  Locksmith_WhenReadLockReleased_ReleasesAllWaitingReaders()
        {
            //- Since we need all of the reader slots in order to run this test, we'll use a separate 
            //  set of lock variables than the rest.
            int     methodsLockState               = 0;  
            object  methodsReadLockObject          = new object();  
            object  methodsWriteLockObject         = new object();  
            int     initialNumberOfThreadsToCreate = Locksmith.MaxNumberOfActiveReaders - 1;
            int     numberWaitingOfThreadsToCreate = 3;
            int     numberOfIncrementingThreads    = initialNumberOfThreadsToCreate + numberWaitingOfThreadsToCreate;
            int     numberOfReads                  = 0;
            ManualResetEvent waitingReadersEvent     = new ManualResetEvent(false); 
            CountdownEvent readersExitedEvent      = new CountdownEvent(initialNumberOfThreadsToCreate); 
            
            Locksmith.EnterReadLock(ref methodsLockState, methodsReadLockObject);
            {
                for (int i = 0; i < initialNumberOfThreadsToCreate; i++)
                {
                    StartNewThreadThatRuns(IncrementNumReadsInsideReadLock);
                }
                
                Thread.Sleep(300);
                Assert.AreEqual(Locksmith.NumberOfActiveReaders(methodsLockState), Locksmith.MaxNumberOfActiveReaders);
                Assert.AreEqual(initialNumberOfThreadsToCreate, numberOfReads);
                
                for (int i = 0; i < numberWaitingOfThreadsToCreate; i++)
                {
                    Thread setValueThread = new Thread(IncrementNumReadsInsideReadLock) { IsBackground = false };
                    setValueThread.Start();
                }
                
                Thread.Sleep(200);
                Assert.That(methodsLockState.HasWaitingReaders(), $"{methodsLockState}");

                waitingReadersEvent.Set(); //- We should be the last one needed to trigger the barrier to drop
                readersExitedEvent.Wait();
                readersExitedEvent.Reset(numberWaitingOfThreadsToCreate);
                
                //- Ensure the waiting readers are still waiting 
                Assert.That(Locksmith.NumberOfActiveReaders(methodsLockState), Is.EqualTo(1)); 
                Assert.AreEqual(numberOfReads, initialNumberOfThreadsToCreate);
                Assert.That(methodsLockState.HasWaitingReaders(), $"{methodsLockState}");
            }
            Locksmith.ExitReadLock(ref methodsLockState, methodsReadLockObject, methodsWriteLockObject); //- This should release the waiting readers
            
            
            //- Give them time to wake up and do their work.
            readersExitedEvent.Wait();

            //- Check the outcome to make sure they woke up and incremented the count.
            Assert.That(numberOfReads, Is.EqualTo(numberOfIncrementingThreads));
            
            return;
            
            
            void IncrementNumReadsInsideReadLock()
            {
                Locksmith.EnterReadLock(ref methodsLockState, methodsReadLockObject);
                {
                    Interlocked.Increment(ref numberOfReads);
                    waitingReadersEvent.WaitOne();
                }
                Locksmith.ExitReadLock(ref methodsLockState, methodsReadLockObject, methodsWriteLockObject);

                readersExitedEvent.Signal();
            }
        }

        
        [Test, Order(6)]
        public void Locksmith_WhileReadLocked_PreventsWritersFromEntering()
        {

            const int failureValue  = 25;
            const int expectedValue = 10;
            int       writeResult   = expectedValue;
            
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            {
                StartNewThreadThatRuns(SetWriteResultToFailureValue);
                Thread.Sleep(1000);
                Assert.That(writeResult, Is.EqualTo(expectedValue));
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void SetWriteResultToFailureValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                writeResult = failureValue;
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }

        [Test, Order(7)]
        public void Locksmith_WhenReadLockReleased_ReleasesAWaitingWriterIfPresent()
        {
            const int initialValue  = 10;
            const int successValue  = 25;
                  int writeResult   = initialValue;
                  ManualResetEvent writerExitedEvent = new ManualResetEvent(false);
            
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            {
                StartNewThreadThatRuns(SetWriteResultToFailureValue);
                Thread.Sleep(50);
                Assert.That(WritersAreWaiting);
                Assert.That(writeResult, Is.EqualTo(initialValue));
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

            writerExitedEvent.WaitOne();
            Assert.That(writeResult, Is.EqualTo(successValue));
            
            return;
            
            
            void SetWriteResultToFailureValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                {
                    writeResult = successValue;
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

                writerExitedEvent.Set();
            }
        }
        
        [Test, Order(8)]
        public void Locksmith_WhenReadLockReleased_ReleasesOnlyOneWaitingWriter()
        {
            const int              initialValue      = 10;
            const int              valueToAdd        = 15;
            const int              successValue      = initialValue + valueToAdd;
                  int              result            = initialValue;
                  ManualResetEvent resultTestedEvent = new ManualResetEvent(false); 
            
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            {
                StartNewThreadThatRuns(SetWriteResultToFailureValue);
                StartNewThreadThatRuns(SetWriteResultToFailureValue);
                Thread.Sleep(300);
                Assert.That(NumberOfWaitingWriters, Is.EqualTo(2));
                Assert.That(result, Is.EqualTo(initialValue));
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
            
            Thread.Sleep(500);
            Assert.That(result, Is.EqualTo(successValue));
            resultTestedEvent.Set();

            return;
            
            
            void SetWriteResultToFailureValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                {
                    int currentResult;
                    int valueToSet;
                
                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueToAdd;
                    } 
                    while (Interlocked.CompareExchange(ref result, valueToSet, currentResult) != currentResult);
                    
                    resultTestedEvent.WaitOne();
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }


        [Test, Order(9)]
        public void Locksmith_WhenReadLockedAndReservedForWriter_CausesNewReadersToWait()
        {
            const int        initialValue       = 10;
            const int        valueAddedByReader = 50;
            const int        valueAddedByWriter = 20;
            int              result             = initialValue;
            CountdownEvent exitedLocksEvent = new CountdownEvent(2);

            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            {
                StartNewThreadThatRuns(AddWriterValue);
                Thread.Sleep(300);
                StartNewThreadThatRuns(AddReaderValue);
                Thread.Sleep(300);
                Assert.That(result, Is.EqualTo(initialValue));
                Assert.That(lockState.IsReservedForWriter());
                Assert.That(lockState.HasWaitingReaders());
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
            
            exitedLocksEvent.Wait();

            return;
            
            void AddReaderValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);  //- You passed the wrong object ya dolt...
                {
                    int currentResult;
                    int valueToSet;
                
                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueAddedByReader;
                    } while (Interlocked.CompareExchange(ref result, valueToSet, currentResult) != currentResult);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

                exitedLocksEvent.Signal();
            }
            
            void AddWriterValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                {
                    int currentResult, valueToSet;
 
                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueAddedByWriter;
                    } 
                    while (Interlocked.CompareExchange(ref result, valueToSet, currentResult) !=  currentResult);
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
                
                exitedLocksEvent.Signal();
            }
        }

        [Test, Order(10)]
        public void Locksmith_WhenReadLockReleased_ReleasesWaitingWritersBeforeWaitingReaders()
        {
            const int              initialValue         = 10;
            const int              valueAddedByReader   = 50;
            const int              valueAddedByWriter   = 20;
            const int              successValue         = initialValue + valueAddedByWriter;
                  int              result               = initialValue;
                  ManualResetEvent valueTestedEvent     = new ManualResetEvent(false);
                  ManualResetEvent valueSetEvent        = new ManualResetEvent(false);
                  CountdownEvent   exitedLocksEvent     = new CountdownEvent(2);
            
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            {
                StartNewThreadThatRuns(AddWriterValue);
                Thread.Sleep(300);
                StartNewThreadThatRuns(AddReaderValue);
                Thread.Sleep(300);
                Assert.That(result, Is.EqualTo(initialValue));
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

            valueSetEvent.WaitOne();
            Assert.That(result, Is.EqualTo(successValue));
            valueTestedEvent.Set();
            exitedLocksEvent.Wait();

            return;
            
            
            void AddReaderValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    int currentResult, valueToSet;

                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueAddedByReader;
                    } 
                    while (Interlocked.CompareExchange(ref result, valueToSet, currentResult) != currentResult);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
                
                exitedLocksEvent.Signal();
            }
            
            void AddWriterValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                {
                    int currentResult;
                    int valueToSet;

                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueAddedByWriter;
                    } 
                    while (Interlocked.CompareExchange(ref result, valueToSet, currentResult)  !=  currentResult);
                    
                    valueSetEvent.Set();
                    valueTestedEvent.WaitOne();
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
                
                exitedLocksEvent.Signal();
            }
        }

        [Test, Order(11)]
        public void Locksmith_WhileWriteLocked_PreventsReadersFromEntering()
        {
            const int failureValue  = 25;
            const int expectedValue = 10;
            int       readResult    = expectedValue;
            
            
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                StartNewThreadThatRuns(SetReadResultToFailureValue);
                Thread.Sleep(1000);
                Assert.That(readResult, Is.EqualTo(expectedValue));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void SetReadResultToFailureValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                readResult = failureValue;
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }


        [Test, Order(12)]
        public void Locksmith_WhenWriteLockReleased_ReleasesAWaitingReaderIfPresent()
        {
            const int initialValue  = 10;
            const int expectedValue = 42;
                  int readResult    = initialValue;
                  ManualResetEvent readerExitedEvent = new ManualResetEvent(false);

            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                StartNewThreadThatRuns(SetReadResultToExpectedValue);
                Thread.Sleep(100);
                Assert.That(ReadersAreWaiting);
                Assert.That(readResult, Is.EqualTo(initialValue));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            
            //- This should be more than enough time for the other thread to wake up and set the value.
            readerExitedEvent.WaitOne();
            Assert.That(readResult, Is.EqualTo(expectedValue));

            return;
            
            
            void SetReadResultToExpectedValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    readResult = expectedValue;
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

                readerExitedEvent.Set();
            }
        }
        
        [Test, Order(13)]
        public void Locksmith_WhenWriteLockReleased_ReleasesAllWaitingReaders()
        {
            int numberOfThreadsToCreate = 100;
            int numberOfReads           = 0;
            CountdownEvent readersExitedEvent = new CountdownEvent(numberOfThreadsToCreate);
            
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                for (int i = 0; i < numberOfThreadsToCreate; i++)
                {
                    StartNewThreadThatRuns(IncrementNumReadsInsideReadLock);
                }

                Thread.Sleep(1000);
                Assert.That(numberOfReads, Is.EqualTo(0));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            readersExitedEvent.Wait();
            Assert.That(numberOfReads, Is.EqualTo(numberOfThreadsToCreate));

            return;

            
            void IncrementNumReadsInsideReadLock()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    Interlocked.Increment(ref numberOfReads);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

                readersExitedEvent.Signal();
            }
        }

        [Test, Order(14)]
        public void Locksmith_WhileWriteLocked_PreventsOtherWritersFromEntering()
        {
            const int initialValue     = 10;
            const int expectedValue    = 42;
                  int valueBeingTested = initialValue;
            
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                StartNewThreadThatRuns(SetValueBeingTestedToExpectedValue);
                Thread.Sleep(1000);
                Assert.That(valueBeingTested, Is.EqualTo(initialValue));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            
            void SetValueBeingTestedToExpectedValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                valueBeingTested = expectedValue;
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }
        
        [Test, Timeout(1000), Order(15)]
        public void Locksmith_WhenWriteLockReleased_ReleasesAWaitingWriterIfPresent()
        {
            const int              initialValue      = 10;
            const int              expectedValue     = 42;
                  int              valueBeingTested  = initialValue;
                  ManualResetEvent writerExitedEvent = new ManualResetEvent(false);

            
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                StartNewThreadThatRuns(SetValueBeingTestedToExpectedValue);
                //- We'll wait a while to give the other thread a chance to set the value, so we can see if the lock works.
                Thread.Sleep(100);
                Assert.That(WritersAreWaiting);
                Assert.That(valueBeingTested, Is.EqualTo(initialValue));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            
            //- This should be more than enough time for the other thread to wake up and set the value.
            writerExitedEvent.WaitOne();
            Assert.That(valueBeingTested, Is.EqualTo(expectedValue));
            
            return;
            
            
            void SetValueBeingTestedToExpectedValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                {
                    valueBeingTested = expectedValue;
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

                writerExitedEvent.Set();
            }
        }

        [Test, Order(16)]
        public void Locksmith_WhenWriteLockReleased_ReleasesWaitingWritersBeforeWaitingReaders()
        {
            const int              initialValue       = 10;
            const int              valueAddedByReader = 50;
            const int              valueAddedByWriter = 20;
            const int              successValue       = initialValue + valueAddedByWriter;
                  int              result             = initialValue;
                  ManualResetEvent valueSetEvent      = new ManualResetEvent(false);
                  ManualResetEvent valueTestedEvent   = new ManualResetEvent(false);
            
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            {
                StartNewThreadThatRuns(AddWriterValue);
                Thread.Sleep(200);
                Assert.That(WritersAreWaiting);
                StartNewThreadThatRuns(AddReaderValue);
                Thread.Sleep(200);
                Assert.That(ReadersAreWaiting);
                Assert.That(result, Is.EqualTo(initialValue));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            valueSetEvent.WaitOne();
            Assert.That(ReadersAreWaiting);
            Assert.That(WritersAreWaiting == false);
            Assert.That(result, Is.EqualTo(successValue));
            valueTestedEvent.Set();

            return;
            
            
            void AddReaderValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    int currentResult, valueToSet;

                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueAddedByReader;
                    } while (Interlocked.CompareExchange(ref result, valueToSet, currentResult) != currentResult);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
            
            void AddWriterValue()
            {
                Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
                {
                    int currentResult;
                    int valueToSet;

                    do
                    {
                        currentResult = result;
                        valueToSet    = currentResult + valueAddedByWriter;
                    } 
                    while (Interlocked.CompareExchange(ref result, valueToSet, currentResult)  !=  currentResult);

                    valueSetEvent.Set();
                    valueTestedEvent.WaitOne();
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }


        [Test, Timeout(3000), Order(16)]
        public void Locksmith_WhenAReaderHasDecidedToWait_WillNotDeadlockIfTheReaderCantCallWaitBeforeAWriterExits()
        {
            const int initialValue     = 10;
            const int expectedValue    = 42;
                  int valueBeingTested = initialValue;
                  ManualResetEvent readerExitedEvent = new ManualResetEvent(false);

            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            
            lock (readLockTestObject)  //- This should prevent the reader from being able to lock the same object, which it needs to do in order to call Monitor.Wait()
            {
                StartNewThreadThatRuns(SetReadResultToTestInt);
                //- Hold the lock long enough that the reader will decide to start waiting 
                Thread.Sleep(500); 
                Assert.That(ReadersAreWaiting);                        
                //- Make sure they didn't manage to set the value despite the lock somehow.
                Assert.That(valueBeingTested, Is.EqualTo(initialValue));                        
                //- Exit the write lock, which should call Monitor.PulseAll().
                //  lock() is re-entrant so the fact that we already locked the read lock object,
                //  shouldn't prevent this thread from reacquiring the lock to call Monitor.PulseAll().
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);  
            }
            
            //- Give the reader time to work if they managed to avoid the deadlock
            readerExitedEvent.WaitOne();            
            Assert.That(valueBeingTested, Is.EqualTo(expectedValue));

            return;
            
            
            void SetReadResultToTestInt()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    valueBeingTested = expectedValue;
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

                readerExitedEvent.Set();
            }
        }

        [Test, Order(17)]
        public void Locksmith_WhenReadLockIsUpgradedWithOnlyOneReader_PreventsNewReadersFromEntering()
        {
            const int failureValue  = 25;
            const int expectedValue = 10;
                  int result        = expectedValue;
            
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            Locksmith.Upgrade(ref lockState, writeLockTestObject);
            {
                StartNewThreadThatRuns(SetReadResultToFailureValue);
                Thread.Sleep(1000);
                Assert.That(result, Is.EqualTo(expectedValue));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void SetReadResultToFailureValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                result = failureValue;
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
        }
        
        [Test, Order(18)]
        public void Locksmith_WhenReadLockIsUpgradedWhileOtherReadersAreActive_WaitsUntilAllOtherReadersExitToConvertLock()
        {
            const int              numberOfOtherReaders = 10;
                  int              numberOfReads        = 0;
                  int              numberOfExits        = 0;
                  CountdownEvent   readEvent            = new CountdownEvent(numberOfOtherReaders);
                  CountdownEvent   exitEvent            = new CountdownEvent(numberOfOtherReaders);
                  ManualResetEvent upgradeEvent         = new ManualResetEvent(false);

            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            
            for (int i = 0; i < numberOfOtherReaders; i++)
            {
                StartNewThreadThatRuns(IncrementNumReadsInsideReadLock);
            }

            readEvent.Wait();
            Assert.That(numberOfReads, Is.EqualTo(numberOfOtherReaders));
            upgradeEvent.Set();

            Locksmith.Upgrade(ref lockState, writeLockTestObject);
            {
                Assert.That(lockState.IsReadLocked() == false, $"{lockState}");
                exitEvent.Wait();
                Assert.That(numberOfExits, Is.EqualTo(numberOfOtherReaders));
            }
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void IncrementNumReadsInsideReadLock()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    Interlocked.Increment(ref numberOfReads);
                    readEvent.Signal();
                    upgradeEvent.WaitOne();
                    Thread.Sleep(1000);
                    Assert.That(ReservedForUpgrade && IsReadLocked);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
                
                Interlocked.Increment(ref numberOfExits);
                exitEvent.Signal();
            }
        }

        [Test, Order(19)]
        public void Locksmith_WhenReadLockIsUpgradedWhileOtherReadersAreActive_PreventsNewReadersFromEntering()
        {
            const int      numberOfOtherReaders   = 10;
            int            numberOfReadersEntered = 0;
            int            numberOfExits          = 0;
            CountdownEvent readEvent              = new CountdownEvent(numberOfOtherReaders);
            CountdownEvent exitEvent              = new CountdownEvent(numberOfOtherReaders);
            ManualResetEvent outerUpgradeEvent    = new ManualResetEvent(false);
            ManualResetEvent innerUpgradeEvent    = new ManualResetEvent(false);

            for (int i = 0; i < numberOfOtherReaders; i++)
            {
                StartNewThreadThatRuns(IncrementNumReadsInsideReadLock_FirstSet);
            }

            readEvent.Wait();
            Assert.That(numberOfReadersEntered, Is.EqualTo(numberOfOtherReaders));
            StartNewThreadThatRuns(EnterReadLockAndUpgrade);
            exitEvent.Wait();
            
            Assert.That(numberOfExits, Is.EqualTo(numberOfOtherReaders));

            for (int i = 0; i < numberOfOtherReaders; i++)
            {
                StartNewThreadThatRuns(IncrementNumReadsInsideReadLock_SecondSet);
            }

            Thread.Sleep(1000);
            Assert.That(numberOfReadersEntered, Is.EqualTo(numberOfOtherReaders));
            innerUpgradeEvent.Set();
            
            return;

            void EnterReadLockAndUpgrade()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                outerUpgradeEvent.Set();
                Locksmith.Upgrade(ref lockState, writeLockTestObject);
                {
                    innerUpgradeEvent.WaitOne();
                    Assert.That(numberOfExits, Is.EqualTo(numberOfOtherReaders));
                }
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
            }
            
            void IncrementNumReadsInsideReadLock_FirstSet()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    Interlocked.Increment(ref numberOfReadersEntered);
                    readEvent.Signal();
                    outerUpgradeEvent.WaitOne();
                    Thread.Sleep(1000);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
                
                Interlocked.Increment(ref numberOfExits);
                exitEvent.Signal();
            }
            
                        
            void IncrementNumReadsInsideReadLock_SecondSet()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    Interlocked.Increment(ref numberOfReadersEntered);
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
                
                Interlocked.Increment(ref numberOfExits);
            }
        }

        public void Locksmith_WhenReadLockIsUpgraded_PreventsNewWritersFromEntering()
        {
            
        }
        
        [Test, Order(20)]
        public void Locksmith_WhenWriteLockIsDowngraded_AllowsNewReadersToEnter()
        {
            const int              successValue        = 25;
            const int              initialValue        = 10;
                  int              readResult          = initialValue;
                  ManualResetEvent readerFinishedEvent = new ManualResetEvent(false);
            
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            Locksmith.Downgrade(ref lockState, readLockTestObject);
            {
                StartNewThreadThatRuns(SetReadResultToFailureValue);
                readerFinishedEvent.WaitOne();
                Assert.That(readResult, Is.EqualTo(successValue));
            }
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void SetReadResultToFailureValue()
            {
                Locksmith.EnterReadLock(ref lockState, readLockTestObject);
                {
                    readResult = successValue;
                }
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

                readerFinishedEvent.Set();
            }
        }

        [Test, Order(21)]
        public void Locksmith_WhenReadLocked_ThrowsIfExitWriteLockIsCalled()
        {
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            Assert.Throws<SynchronizationLockException>(ExitLockWithExitWriteLock);
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);


            return;
            
            
            void ExitLockWithExitWriteLock() =>
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
        
        [Test, Order(22)]
        public void Locksmith_WhenWriteLocked_ThrowsIfExitReadLockIsCalled()
        {
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            Assert.Throws<SynchronizationLockException>(ExitLockWithExitReadLock);
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void ExitLockWithExitReadLock() =>
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
        }

        [Test, Order(23)]
        public void Locksmith_WhenReadLockIsUpgraded_ThrowsIfExitReadLockIsCalled()
        {
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            Locksmith.Upgrade(ref lockState, writeLockTestObject);
            Assert.Throws<SynchronizationLockException>(TryToCallExitReadLock);
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void TryToCallExitReadLock() =>
                Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
        
        [Test, Order(23)]
        public void Locksmith_WhenWriteLockIsDowngraded_ThrowsIfExitWriteLockIsCalled()
        {
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            Locksmith.Downgrade(ref lockState, readLockTestObject);
            Assert.Throws<SynchronizationLockException>(TryToCallExitWriteLock);
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);

            return;
            
            
            void TryToCallExitWriteLock() =>
                Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
        
        //- Some more tests we should add:
        //- public void Locksmith_WhenWriteLockIsDowngraded_ThrowsIfDowngradeIsCalledAgain()
        //- public void Locksmith_WhenWriteLockIsDowngradedWithNoWaitingWriters_ReleasesWaitingReadersIfPresent()
        //- public void Locksmith_WhenReadLockIsUpgraded_ThrowsIfUpgradeIsCalledAgain()
        //- public void Locksmith_WhenReadLockAttemptsUpgradeButAlreadyReserved_ReturnsFalse()
        //- public void Locksmith_WhenReadLockIsUpgradedSuccessfully_ReturnsTrue()
        //- public void Locksmith_WhenNotReadLocked_ThrowsIfExitReadLockIsCalled()
        //- public void Locksmith_WhenNotWriteLocked_ThrowsIfExitWriteLockIsCalled()


        private void StartNewThreadThatRuns(ThreadStart delegateToRun)
        {
            _ = delegateToRun ?? throw new ArgumentNullException(nameof(delegateToRun));
            
            Thread setValueThread = new Thread(delegateToRun) {IsBackground = false};
            setValueThread.Start();
        }
        
        void SetIntToValueInsideReadLock(ref int intToSet, int valueToSetTo)
        {
            Locksmith.EnterReadLock(ref lockState, readLockTestObject);
            intToSet = valueToSetTo;
            Locksmith.ExitReadLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
        
        void SetIntToValueInsideWriteLock(ref int intToSet, int valueToSetTo)
        {
            Locksmith.EnterWriteLock(ref lockState, writeLockTestObject);
            intToSet = valueToSetTo;
            Locksmith.ExitWriteLock(ref lockState, readLockTestObject, writeLockTestObject);
        }
    }
}