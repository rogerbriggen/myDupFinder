// Roger Briggen license this file to you under the MIT license.
//

using System.IO;

namespace RogerBriggen.MyDupFinderData;

public class FileHelper
{
    public static bool EndsWithDirectoryDelimiter(string pathToCheck)
    {
        if (pathToCheck.EndsWith(Path.DirectorySeparatorChar))
        {
            return true;
        }
        if (pathToCheck.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return true;
        }
        return false;
    }

    public static string AddDirectoryDelimiter(string path)
    {
        if (EndsWithDirectoryDelimiter(path))
        {
            //Nothing to do...
            return path;
        }
        return path + Path.DirectorySeparatorChar;
    }
}
