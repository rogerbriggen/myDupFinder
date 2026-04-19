// Roger Briggen license this file to you under the MIT license.
//

using System.IO;
using RogerBriggen.MyDupFinderData;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest;

public class ScanItemTest
{
    [Fact]
    public void FilenameTest()
    {
        ScanItemDto scanItem = new ScanItemDto();
        scanItem.FilenameAndPath = Path.Combine("o:", "test", "diesIstEinTest", "meineDatei.txt");
        Assert.Equal("meineDatei.txt", scanItem.Filename);
        scanItem.FilenameAndPath = Path.Combine("o:", "test", "diesIstEinTest", "nochEineDatei.txt");
        Assert.Equal("nochEineDatei.txt", scanItem.Filename);
        scanItem.FilenameAndPath = Path.Combine("o:", "test", "diesIstEinTest", "meineDatei");
        Assert.Equal("meineDatei", scanItem.Filename);
    }
}
