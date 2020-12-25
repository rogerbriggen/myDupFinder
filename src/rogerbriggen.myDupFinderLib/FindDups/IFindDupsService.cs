using RogerBriggen.MyDupFinderData;

namespace RogerBriggen.MyDupFinderLib
{

    public interface IFindDupsService : IService
    {
      
        void StartScan(MyDupFinderFindDupsJobDTO findDupsJobDTO);
        void StopScan(string basePath);
    }
}
