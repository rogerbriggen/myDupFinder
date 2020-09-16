﻿using System.IO;
using System.Xml;

namespace RogerBriggen.MyDupFinderLib
{
    public static class MyDupFinderProject
    {
        public static void ReadConfigurationFromFile(string pathAndFilename, out MyDupFinderProjectDTO myDupFinderProjetDTO)
        {
            System.Xml.Serialization.XmlSerializer x = new System.Xml.Serialization.XmlSerializer(typeof(MyDupFinderProjectDTO));
            using (var stream = new FileStream(pathAndFilename, FileMode.Open, FileAccess.Read))
            using (var reader = XmlReader.Create(stream, null))
            {
                myDupFinderProjetDTO = (MyDupFinderProjectDTO)x.Deserialize(reader);
            }
        }


        public static void WriteConfigurationToFile(MyDupFinderProjectDTO myDupFinderProjetDTO, string pathAndFilename)
        {
            System.Xml.Serialization.XmlSerializer x = new System.Xml.Serialization.XmlSerializer(myDupFinderProjetDTO.GetType());
            using (FileStream fs = new FileStream(pathAndFilename, FileMode.CreateNew, FileAccess.Write))
            {
                x.Serialize(fs, myDupFinderProjetDTO);
            }
        }

        public static void getExampleDTO(out MyDupFinderProjectDTO myDupFinderProjectDTO)
        {
            //project
            myDupFinderProjectDTO = new MyDupFinderProjectDTO();

            //scan
            var myDupFinderScanJobDTO = new MyDupFinderScanJobDTO();
            myDupFinderScanJobDTO.JobName = "Example scanjob name";
            myDupFinderScanJobDTO.OriginComputer = "E6600";
            myDupFinderScanJobDTO.BasePath = @"m:\Test";
            myDupFinderProjectDTO.MyDupFinderScanJobDTOs.Add(myDupFinderScanJobDTO);

            //check
            var myDupFinderCheckJobDTO = new MyDupFinderCheckJobDTO();
            myDupFinderCheckJobDTO.ScanJobDTO = myDupFinderScanJobDTO;
            myDupFinderCheckJobDTO.ScanJobDTO.JobName = "Example checkjob name";
            myDupFinderCheckJobDTO.IgnoreBasePath = false;
            myDupFinderCheckJobDTO.SkipHashCheck = true;
            myDupFinderProjectDTO.MyDupFinderCheckJobDTOs.Add(myDupFinderCheckJobDTO);

            //FindDups
            var myDupFinderFindDupsJobDTO = new MyDupFinderFindDupsJobDTO();
            myDupFinderFindDupsJobDTO.JobName = "Example FindDupsJob name";
            myDupFinderFindDupsJobDTO.FindDupsMode = MyDupFinderFindDupsJobDTO.EFindDupsMode.FindOnlyDups;
            myDupFinderFindDupsJobDTO.DatabaseFileBase = @"m:\finddupdb\base.db";
            myDupFinderFindDupsJobDTO.DatabaseFile = @"m:\finddupdb\newdb.db";
            myDupFinderProjectDTO.MyDupFinderFindDupsJobDTOs.Add(myDupFinderFindDupsJobDTO);
        }
    }
}