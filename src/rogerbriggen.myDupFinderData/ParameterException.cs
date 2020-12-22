// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderData
{
    public class ParameterException : SystemException
    {
        public string ParamName { get; }
        
        public ParameterException()
        {
            ParamName = "";
        }

        public ParameterException(String message, String paramName) : base(message)
        {
            ParamName = paramName;
        }

        public ParameterException(String message, String paramName, Exception innerException) : base(message, innerException)
        {
            ParamName = paramName;
        }
    }
}
