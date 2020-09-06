using rogerbriggen.myDupFinderLib;
using Xunit;

namespace rogerbriggen.myDupFinderLibUnitTest
{
    public class ScanItemTest
    {
        [Fact]
        public void Test1()
        {
            ScanItem scanItem = new ScanItem();
            Assert.True(scanItem.MyTest());
        }
    }
}
