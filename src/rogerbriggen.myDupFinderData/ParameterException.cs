// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderData
{
    public class ParameterException : SystemException
    {
        
     
        public ParameterException(String message) : base(message)
        {
            
        }

        public ParameterException(String message, Exception innerException) : base(message, innerException)
        {
            
        }
    }
}
