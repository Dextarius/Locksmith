using System;
using System.Threading;

namespace Dextarius
{
    public static class Threading
    {
        //- Apparently, when the author of the article mentioned in the Locksmith class was testing
        //  their lock, for whatever reason, incrementing a dummy value while spinning had a
        //  positive effect on performance.  We'll have to run some tests to see how it effects this code,
        //  but I don't see any harm in adding one until we get around to it
        static          int  dummyValue;
        static readonly bool IsMultiProcessor = (Environment.ProcessorCount > 1);

        public static void Spin(int spinCount)
        {
            if      (spinCount < 5 && IsMultiProcessor) Thread.SpinWait(20 * spinCount);
            else if (spinCount < 10)                    Thread.Yield(); 
            else                                        Thread.Sleep(1);
            dummyValue++;
        }

        public static void BriefWait()
        {
            if (IsMultiProcessor) Thread.SpinWait(1);
            else                  Thread.Yield();
            
            dummyValue++;
        }
    }
}