using System.Collections.Generic;

namespace RogerBriggen.MyDupFinderLib
{
    public class MyDupFinderProjectDTO
    {
        public MyDupFinderProjectDTO()
        {
            MyDupFinderScanJobDTOs = new List<MyDupFinderScanJobDTO>();
            MyDupFinderCheckJobDTOs = new List<MyDupFinderCheckJobDTO>();
            MyDupFinderFindDupsJobDTOs = new List<MyDupFinderFindDupsJobDTO>();
        }
        public List<MyDupFinderScanJobDTO> MyDupFinderScanJobDTOs { get; }
        public List<MyDupFinderCheckJobDTO> MyDupFinderCheckJobDTOs { get; }
        public List<MyDupFinderFindDupsJobDTO> MyDupFinderFindDupsJobDTOs { get; }
    }
}
