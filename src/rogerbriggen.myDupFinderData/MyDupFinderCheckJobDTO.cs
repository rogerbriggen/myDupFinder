// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderData;

public class MyDupFinderCheckJobDTO
{
    public MyDupFinderCheckJobDTO() => ScanJobDTO = new MyDupFinderScanJobDTO();
    public MyDupFinderScanJobDTO ScanJobDTO { get; set; }

    public bool IgnoreBasePath { get; set; }

    public bool SkipHashCheck { get; set; }

}
