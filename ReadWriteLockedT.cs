
namespace Dextarius
{
    /// <summary>
    ///     A class that enforces a read/write lock over a particular value.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <remarks>
    /// 
    /// </remarks>
    public class ReadWriteLocked<T> 
    {
        //- We cannot use the lockedValue as a parameter for the locking methods
        //  because the objects passed have to be the same every time the lock is used.
        private object readLockobject = new object();
        private int    lockState      = 0;
        private T      lockedValue;

        public bool IsReadLocked => lockState.IsReadLocked();
    
        
        //- The class uses 'this' as the writeLockObject parameter when it
        //  calls the locking methods.  This should only done if you can be
        //  absolutely sure no one else is going to call another lock (i.e.
        //  lock() statements, Monitor methods, Locksmith methods) on an
        //  instance of the class.
        public T Value
        {
            get
            {
                T result;
    
                //- I use '{' brackets to keep track of what's inside the locking
                //  methods.
                Locksmith.EnterReadLock(ref lockState, readLockobject);
                {
                    result = lockedValue;
                }
                Locksmith.ExitReadLock(ref lockState, readLockobject, this);
    
                return result;
            }
            set
            {
                Locksmith.EnterWriteLock(ref lockState, this);
                {
                    lockedValue = value;
                }
                Locksmith.ExitWriteLock(ref lockState, readLockobject, this);
            }
        }

        public bool IfValueEqualsFirstSetToSecond(T valueToTestFor, T valueToSetTo)
        {
            Locksmith.EnterReadLock(ref lockState, readLockobject);
            {
                if (lockedValue.Equals(valueToTestFor))
                {
                    Locksmith.Upgrade(ref lockState, this);
                    {
                        lockedValue = valueToSetTo;
                    }
                    Locksmith.ExitWriteLock(ref lockState, readLockobject, this);

                    return true;
                }
            }
            Locksmith.ExitReadLock(ref lockState, readLockobject, this);

            return false;
        }
    
        public ReadWriteLocked(T valueToStore)
        {
            lockedValue = valueToStore;
        }
    }
}