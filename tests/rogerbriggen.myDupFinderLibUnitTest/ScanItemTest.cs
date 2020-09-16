// Roger Briggen license this file to you under the MIT license.
//

using RogerBriggen.MyDupFinderLib;
using Xunit;

namespace RogerBriggen.MyDupFinderLibUnitTest
{

    public class ScanItemTest
    {
        [Fact]
        public void FilenameTest()
        {
            ScanItem scanItem = new ScanItem();
            scanItem.FilenameAndPath = @"o:\test\diesIstEinTest\meineDatei.txt";
            Assert.Equal("meineDatei.txt", scanItem.Filename);
            scanItem.FilenameAndPath = @"o:\test\diesIstEinTest\nochEineDatei.txt";
            Assert.Equal("nochEineDatei.txt", scanItem.Filename);
            scanItem.FilenameAndPath = @"o:\test\diesIstEinTest\meineDatei";
            Assert.Equal("meineDatei", scanItem.Filename);
        }
    }
}
