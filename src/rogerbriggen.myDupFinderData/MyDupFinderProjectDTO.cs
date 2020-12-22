using System.Collections.Generic;

namespace RogerBriggen.MyDupFinderData
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


        public static void CheckSanity(MyDupFinderProjectDTO dto)
        {
            foreach (MyDupFinderScanJobDTO scanJobDto in dto.MyDupFinderScanJobDTOs)
            {
                MyDupFinderScanJobDTO.CheckSanity(scanJobDto);
            }
        }


        public static void FixDto(MyDupFinderProjectDTO dto)
        {
            foreach (MyDupFinderScanJobDTO scanJobDto in dto.MyDupFinderScanJobDTOs)
            {
                MyDupFinderScanJobDTO.FixDto(scanJobDto);
            }
        }
    }

   
}
