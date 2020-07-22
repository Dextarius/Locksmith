using System;
using System.Threading;

namespace Dextarius
{
    public static partial class Locksmith
    {
        /// <summary>
        ///     A class that simplifies using the locks created by the Locksmith class
        ///     Each instance manages the method calls and parameter storage for one lock.
        /// </summary>
        /// <remarks>
        ///    Since it's a reference type, the class also allows for the usage of the
        ///    using(instance.EnterXXXLock()) pattern to handle exiting the lock.
        /// </remarks>
        public class ReaderWriterLock : IDisposable
        {
            private int    lockState;
            private object readLockObject;

            public bool IsReadLocked => lockState.IsReadLocked();


            public ReaderWriterLock ReadLock()
            {
                Locksmith.EnterReadLock(ref lockState, readLockObject);

                return this;
            }

            public ReaderWriterLock WriteLock()
            {
                Locksmith.EnterWriteLock(ref lockState, this);

                return this;
            }

            public ReaderWriterLock Upgrade()
            {
                Locksmith.Upgrade(ref lockState, this);

                return this;
            }

            public ReaderWriterLock Downgrade()
            {
                Locksmith.EnterReadLock(ref lockState, readLockObject);

                return this;
            }

            //- TODO : Since this is an explicit implementation, it the only thing that should
            //  end up calling it is a using() statement.  Still, we should weigh the convenience
            //  of this against the fact that it could create harder to find errors if someone
            //  used the lock in a way I didn't anticipate, and ends up calling this at the wrong time.
            void IDisposable.Dispose()
            {
                if (lockState.IsReadLocked())
                {
                    Locksmith.ExitReadLock(ref lockState, readLockObject, this);
                }
                else if (lockState.IsWriteLocked())
                {
                    Locksmith.ExitWriteLock(ref lockState, readLockObject, this);
                }
                else
                {
                    throw new SynchronizationLockException(
                        $"A process used the Dispose() method of a {nameof(ReaderWriterLock)} to try to exit a lock, " +
                        $"but the lock was not in a locked state");
                }
            }

            public ReaderWriterLock(object objectToLock)
            {
                readLockObject = objectToLock;
                lockState      = 0;
            }
        }
    }
}


