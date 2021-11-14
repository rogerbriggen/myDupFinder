// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.IO;

namespace RogerBriggen.MyDupFinderData;

public class ScanErrorItemDto
{
    public ScanErrorItemDto(ScanItemDto si, Exception myException, DateTime dateRunStartedUTC)
    {
        FilenameAndPath = si.FilenameAndPath;
        PathBase = si.PathBase;
        ScanExecutionComputer = si.ScanExecutionComputer;
        OriginComputer = si.OriginComputer;
        ScanName = si.ScanName;
        FileSize = si.FileSize;
        FileCreationUTC = si.FileCreationUTC;
        FileLastModificationUTC = si.FileLastModificationUTC;
        ErrorOccurrence = DateTime.Now;
        MyException = myException.ToString();
        DateRunStartedUTC = dateRunStartedUTC;
    }

    /// <summary>
    /// Constructor with NoParams for EF Core
    /// </summary>
    public ScanErrorItemDto() => MyException = "";

    /// <summary>
    /// ID for database (Primary Key)
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The complete path to the file.
    /// </summary>
    private string filenameAndPath = string.Empty;

    /// <summary>
    /// Gets or sets the complete path to the file.
    /// </summary>
    public string FilenameAndPath { get => filenameAndPath; set { SetFilenameHelper(value); filenameAndPath = value; } }

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

    /// <summary>
    /// User provided Name for the scan (i.e original, backup of xxx...)
    /// </summary>
    public string ScanName { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// File creation date in UTC
    /// </summary>
    public DateTime FileCreationUTC { get; set; }

    /// <summary>
    /// Last modifiaction date of the file in UTC
    /// </summary>
    public DateTime FileLastModificationUTC { get; set; }

    /// <summary>
    /// DateTime when the error occured
    /// </summary>
    public DateTime ErrorOccurrence { get; set; }

    /// <summary>
    /// The exception text
    /// </summary>
    public string MyException { get; set; }

    /// <summary>
    /// The date and time the run was started. Can be used to group the errors together from one run
    /// </summary>
    public DateTime DateRunStartedUTC { get; set; }

    private void SetFilenameHelper(string filenameAndPath) => Filename = Path.GetFileName(filenameAndPath);
}


