// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;

namespace RogerBriggen.MyDupFinderLib
{
    public class ScanItem
    {
        /// <summary>
        /// The complete path to the file.
        /// </summary>
        private string filenameAndPath = string.Empty;

        /// <summary>
        /// Gets or sets the complete path to the file.
        /// </summary>
        public string FilenameAndPath { get { return filenameAndPath; } set { SetFilenameHelper(value); filenameAndPath = value; } }

        /// <summary>
        /// Gets the filename (must be the same as in FilenameAndPath), but is for easier checking.
        /// </summary>
        public string Filename { get; private set; } = string.Empty;

        /// <summary>
        /// Gets or Sets the base path part, so we can move the file later on and still finding them.
        /// </summary>
        public string PathBase { get; set; } = string.Empty;

        /// <summary>
        /// Gets or Sets the computer name, where the scan is executed
        /// </summary>
        public string ScanExecutionComputer { get; set; } = string.Empty;

        /// <summary>
        /// Gets or Sets the computer name, where the file is located. (User provided)
        /// </summary>
        public string OriginComputer { get; set; } = string.Empty;

        public long FileSize { get; set; }

        public DateTime FileCreationUTC { get; set; }

        public DateTime FileLastModificationUTC { get; set; }

        public string FileSha512Hash { get; set; } = string.Empty;

        /// <summary>
        /// Gets or Sets the date, when the original entry was created and the hash was calculated and the file size read.
        /// </summary>
        public DateTime FirstScanDateUTC { get; set; }

        /// <summary>
        /// Gets or Sets the date, when the last time a check was executed (with or without hash).
        /// </summary>
        public DateTime LastScanDateUTC { get; set; }

        /// <summary>
        /// Gets or Sets the date, when the last time a check with hash calculation was executed.
        /// </summary>
        public DateTime LastSha512ScanDateUTC { get; set; }

        private void SetFilenameHelper(string filenameAndPath)
        {
            Filename = Path.GetFileName(filenameAndPath);
        }
    }
}
