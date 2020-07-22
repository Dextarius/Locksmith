# Locksmith
A static class which allows the user to create performant read/write locks around sections of code in a similar manner to Monitor.Enter()/Monitor.Exit()

**WARNING**: This was designed as a proof of concept and should not be used by anything important.   Multi-threaded code is already a headache to debug, don't add to the pain.  Also, if you intend to read the code itself, please see the note at the bottom of the page.

<br>

### Some features of the locks include:

- They are both upgradable and downgradable

- Unlike ReaderWriterLockSlim they can be upgraded/downgraded directly from their respective read/write locks, they do not require a special type of lock.

- A combination of atomic reads/writes, and spinning is used to minimize the amount of time any thread spends blocking.

- The locks themselves have no overhead; the only memory used is the parameters you pass.  This means that if you have a class with a reference type field that can be locked, you could potentially use the class instance and the field as parameters, and get a read/write lock for the price of a single int field to store the state in.

- Each lock can support 260,000 concurrent active readers, and a (theoretically) infinite number of waiting readers/writers, if you can find a computer to get that high.

- The methods have the option of allowing the locks to be used across threads.

- An included class that handles the storage of the lock variables and method calls if you don’t want to deal with it, as well as providing the equivalent of a lock() statement in the form of using(lock.EnterXXX())

<br>

### Some notable differences from Monitor.Enter/Exit:
- The locks are not reentrant at the moment.  I'm debating how I want to add this, but for now the locks do not support it.

- The methods require more parameters than  the ones in the Monitor class.

- Since the locks have to manage readers and writers, the user has to provide two objects instead of one, as well as a ref argument to an int that can be used to keep track of the lock's state.

<br>

The objects used as arguments in the Locksmith methods have similar requirements to those required to use a lock() statement or the Monitor class methods:
  
- The objects must be the same objects every time you use the methods to create a particular lock.  Basically this is just saying if you used objects A and B to Enter as a reader, you have to use those same objects when you want to exit the lock, or enter that lock as a writer, etc.  Don't use a field that's going to change it's value, because any threads that entered the lock using the old value may deadlock waiting for a notification that requires that old object. 

- Don't use an object that is being used by another lock.  This includes lock statements, the methods in the Monitor class, and other Locksmith "locks".

- The int variable used to hold the lock state should not be modified by any other process.  That value controls the state of the lock, and changing it in the slightest will completely break the lock.

<br>

**Note:** If you want to read the code I will warn you, there are a ton of comments and debugging code in the source, since it's rather important to understand the exact state at any given moment.  If you want to read the code without all of the clutter, there is a copy in Locksmith_Concise.cs
that has all the debugging/extraneous comments cut out.  It's 600 lines shorter...
