﻿/*
    Copyright (C) 2016-2018 Hajin Jang
    Licensed under GPL 3.0
 
    PEBakery is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.

    Additional permission under GNU GPL version 3 section 7

    If you modify this program, or any covered work, by linking
    or combining it with external libraries, containing parts
    covered by the terms of various license, the licensors of
    this program grant you additional permission to convey the
    resulting work. An external library is a library which is
    not derived from or based on this program. 
*/

// #define ENABLE_XZ

using Joveler.ZLibWrapper;
using PEBakery.Exceptions;
using PEBakery.Helper;
using PEBakery.IniLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XZ.NET;

namespace PEBakery.Core
{
    /*
    [Attachment Format]
    Streams are encoded in base64 format.
    Concat all lines into one long string, append '=', '==' or nothing according to length.
    (Need '=' padding to be appended to be .Net acknowledged base64 format)
    Decode base64 encoded string to get binary, which follows these 2 types.
    
    Note)
    All bytes is ordered in little endian.
    WB082-generated zlib magic number always starts with 0x78.
    CodecWBZip is a combination of Type 1 and 2, choosing algorithm based on file extension.

    [Type 1]
    Zlib Compressed File + Zlib Compressed FirstFooter + Raw FinalFooter
    - Used in most file.

    [Type 2]
    Raw File + Zlib Compressed FirstFooter + Raw FinalFooter
    - Used in already compressed file (Ex 7z, zip).

    [Type 3] (PEBakery Only!)
    XZ Compressed File + Zlib Compressed FirstFooter + Raw FinalFooter

    [FirstFooter]
    550Byte (0x226) (When decompressed)
    0x000 - 0x1FF (512B) -> L-V (Length - Value)
        1B : [Length of FileName]
        511B : [FileName]
    0x200 - 0x207 : 8B  -> Length of Raw File
    0x208 - 0x20F : 8B  -> (Type 1) Length of zlib-compressed File
                           (Type 2) Null-padded
                           (Type 3) Length of LZMA-compressed File
    0x210 - 0x21F : 16B -> Null-padded
    0x220 - 0x223 : 4B  -> CRC32 of Raw File
    0x224         : 1B  -> Compress Mode (Type 1 : 00, Type 2 : 01, Type 3 : 02)
    0x225         : 1B  -> Compress Level (Type 1, 3 : 01 ~ 09, Type 2 : 00)

    [FinalFooter]
    Not compressed, 36Byte (0x24)
    0x00 - 0x04   : 4B  -> CRC32 of Zlib-Compressed File and Zlib-Compressed FirstFooter
    0x04 - 0x08   : 4B  -> Unknown - Always 1 
    0x08 - 0x0B   : 4B  -> WB082 ZLBArchive Component version - Always 2
    0x0C - 0x0F   : 4B  -> Zlib Compressed FirstFooter Length
    0x10 - 0x17   : 8B  -> Zlib Compressed File Length
    0x18 - 0x1B   : 4B  -> Unknown - Always 1
    0x1C - 0x23   : 8B  -> Unknown - Always 0
    
    Note) Which purpose do Unknown entries have?
    0x04 : When changed, WB082 cannot recognize filename. Maybe related to filename encoding?
    0x08 : When changed to higher value than 2, WB082 refuses to decompress with error message
        Error Message = $"The archive was created with a different version of ZLBArchive v{value}"
    0x18 : Decompress by WB082 is unaffected by this value
    0x1C : When changed, WB082 thinks the encoded file is corrupted
    
    [Improvement Points]
    - Use LZMA instead of zlib, for ultimate compression rate - DONE
    - Zopfli support in place of zlib, for better compression rate with compability with WB082
    - Design more robust script format. 
    */

    // Possible zlib stream header
    // https://groups.google.com/forum/#!msg/comp.compression/_y2Wwn_Vq_E/EymIVcQ52cEJ

    #region EncodedFile
    public class EncodedFile
    {
        #region Enum EncodeMode 
        public enum EncodeMode : byte
        {
            ZLib = 0x00, // Type 1
            Raw = 0x01, // Type 2
#if ENABLE_XZ
            XZ = 0x02, // Type 3 (PEBakery Only)
#endif
        }

        public EncodeMode ParseEncodeMode(string str)
        {
            EncodeMode mode;
            if (str.Equals("ZLib", StringComparison.OrdinalIgnoreCase))
                mode = EncodeMode.ZLib;
            else if (str.Equals("Raw", StringComparison.OrdinalIgnoreCase))
                mode = EncodeMode.Raw;
#if ENABLE_XZ
            else if (str.Equals("XZ", StringComparison.OrdinalIgnoreCase))
                mode = EncodeMode.XZ;
#endif
            else
                throw new ArgumentException($"Wrong EncodeMode [{str}]");

            return mode;
        }
        #endregion

        #region Const Strings, String Factory
        private const string EncodedFolders = "EncodedFolders";
        private const string AuthorEncoded = "AuthorEncoded";
        private const string InterfaceEncoded = "InterfaceEncoded";
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string GetSectionName(string dirName, string fileName) => $"EncodedFile-{dirName}-{fileName}";
        #endregion

        #region Dict ImageEncodeDict
        public static readonly ReadOnlyDictionary<ImageHelper.ImageType, EncodeMode> ImageEncodeDict = new ReadOnlyDictionary<ImageHelper.ImageType, EncodeMode>(
            new Dictionary<ImageHelper.ImageType, EncodeMode>
            {
                // Auto detect compress algorithm by extension.
                // Note: .ico file can be either raw (bmp) or compressed (png).
                //       To be sure, use EncodeMode.ZLib in .ico file.
                { ImageHelper.ImageType.Bmp, EncodeMode.ZLib },
                { ImageHelper.ImageType.Jpg, EncodeMode.Raw },
                { ImageHelper.ImageType.Png, EncodeMode.Raw },
                { ImageHelper.ImageType.Gif, EncodeMode.Raw },
                { ImageHelper.ImageType.Ico, EncodeMode.ZLib },
                { ImageHelper.ImageType.Svg, EncodeMode.ZLib },
            });
        #endregion

        #region AttachFile
        public static Script AttachFile(Script sc, string dirName, string fileName, string srcFilePath, EncodeMode type = EncodeMode.ZLib)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            using (FileStream fs = new FileStream(srcFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Encode(sc, dirName, fileName, fs, type, false);
            }
        }

        public static Script AttachFile(Script sc, string dirName, string fileName, Stream srcStream, EncodeMode type = EncodeMode.ZLib)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            return Encode(sc, dirName, fileName, srcStream, type, false);
        }

        public static Script AttachFile(Script sc, string dirName, string fileName, byte[] srcBuffer, EncodeMode type = EncodeMode.ZLib)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            return Encode(sc, dirName, fileName, srcBuffer, type, false);
        }
        #endregion

        #region AttachLogo
        public static Script AttachLogo(Script sc, string fileName, string srcFilePath)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));
            if (srcFilePath == null)
                throw new ArgumentNullException(nameof(srcFilePath));

            if (!ImageHelper.GetImageType(srcFilePath, out ImageHelper.ImageType imageType))
                throw new ArgumentException($"Image [{Path.GetExtension(srcFilePath)}] is not supported");

            using (FileStream fs = new FileStream(srcFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Encode(sc, "AuthorEncoded", fileName, fs, ImageEncodeDict[imageType], true);
            }
        }

        public static Script AttachLogo(Script sc, string dirName, string fileName, Stream srcStream, EncodeMode type = EncodeMode.ZLib)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            return Encode(sc, dirName, fileName, srcStream, type, true);
        }

        public static Script AttachLogo(Script sc, string dirName, string fileName, byte[] srcBuffer, EncodeMode type = EncodeMode.ZLib)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            return Encode(sc, dirName, fileName, srcBuffer, type, true);
        }
        #endregion

        #region AddFolder, ContainsFolder
        public static Script AddFolder(Script sc, string folderName, bool overwrite)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));
            if (folderName == null)
                throw new ArgumentNullException(nameof(folderName));

            if (!StringEscaper.IsFileNameValid(folderName, new char[] {'[', ']'}))
                throw new ArgumentException($"[{folderName}] contains invalid character");

            if (!overwrite)
            {
                if (sc.Sections.ContainsKey(folderName))
                    throw new InvalidOperationException($"Section [{folderName}] already exists");
            }
           
            // Write folder name into EncodedFolder (except AuthorEncoded, InterfaceEncoded)
            if (!folderName.Equals(AuthorEncoded, StringComparison.OrdinalIgnoreCase) &&
                !folderName.Equals(InterfaceEncoded, StringComparison.OrdinalIgnoreCase))
            {
                if (sc.Sections.ContainsKey(EncodedFolders))
                {
                    List<string> folders = sc.Sections[EncodedFolders].GetLines();
                    if (folders.FindIndex(x => x.Equals(folderName, StringComparison.OrdinalIgnoreCase)) == -1)
                        Ini.WriteRawLine(sc.RealPath, EncodedFolders, folderName, false);
                }
                else
                {
                    Ini.WriteRawLine(sc.RealPath, EncodedFolders, folderName, false);
                }
            }

            Ini.AddSection(sc.RealPath, folderName);
            return sc.Project.RefreshScript(sc);
        }

        public static bool ContainsFolder(Script sc, string folderName)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));
            if (folderName == null)
                throw new ArgumentNullException(nameof(folderName));

            // AuthorEncoded, InterfaceEncoded is not recorded to EncodedFolders
            if (folderName.Equals(AuthorEncoded, StringComparison.OrdinalIgnoreCase) ||
                folderName.Equals(InterfaceEncoded, StringComparison.OrdinalIgnoreCase))
            {
                return sc.Sections.ContainsKey(folderName);
            }

            if (sc.Sections.ContainsKey(folderName) && sc.Sections.ContainsKey(EncodedFolders))
            {
                List<string> folders = sc.Sections[EncodedFolders].GetLines();
                return folders.FindIndex(x => x.Equals(folderName, StringComparison.OrdinalIgnoreCase)) != -1;
            }

            return false;
        }
        #endregion

        #region ExtractFile, ExtractFolder
        public static long ExtractFile(Script sc, string folderName, string fileName, Stream outStream)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            string section = GetSectionName(folderName, fileName);
            if (!sc.Sections.ContainsKey(section))
                throw new InvalidOperationException($"[{folderName}\\{fileName}] does not exists in [{sc.RealPath}]");

            List<string> encoded = sc.Sections[section].GetLinesOnce();
            return Decode(encoded, outStream);
        }

        public static void ExtractFolder(Script sc, string dirName, string destDir, bool overwrite = false)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            Dictionary<string, string> fileDict;
            switch (sc.Sections[dirName].DataType)
            {
                case SectionDataType.IniDict:
                    fileDict = sc.Sections[dirName].GetIniDict();
                    break;
                case SectionDataType.Lines:
                    fileDict = Ini.ParseIniLinesIniStyle(sc.Sections[dirName].GetLines());
                    break;
                default:
                    throw new InternalException("Internal Logic Error at EncodedFile.ExtractFolder");
            }

            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            foreach (string fileName in fileDict.Keys)
            {
                string destFile = Path.Combine(destDir, fileName);
                if (!overwrite && File.Exists(destFile))
                    throw new InvalidOperationException($"File [{destFile}] cannot be overwritten");

                using (FileStream fs = new FileStream(destFile, FileMode.Create, FileAccess.Write))
                {
                    string section = GetSectionName(dirName, fileName);
                    if (!sc.Sections.ContainsKey(section))
                        throw new InvalidOperationException($"[{dirName}\\{fileName}] does not exists in [{sc.RealPath}]");

                    List<string> encoded = sc.Sections[section].GetLinesOnce();
                    Decode(encoded, fs);
                }
            }
        }
        
        public static MemoryStream ExtractLogo(Script sc, out ImageHelper.ImageType type)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            if (!sc.Sections.ContainsKey(AuthorEncoded))
                throw new InvalidOperationException("Directory [AuthorEncoded] does not exist");

            Dictionary<string, string> fileDict = sc.Sections[AuthorEncoded].GetIniDict();

            if (!fileDict.ContainsKey("Logo"))
                throw new InvalidOperationException($"Logo does not exist in \'{sc.Title}\'");

            string logoFile = fileDict["Logo"];
            if (!ImageHelper.GetImageType(logoFile, out type))
                throw new ArgumentException($"Image [{Path.GetExtension(logoFile)}] is not supported");

            List<string> encoded = sc.Sections[GetSectionName(AuthorEncoded, logoFile)].GetLinesOnce();
            return DecodeInMemory(encoded);
        }

        public static Image ExtractLogoImage(Script sc, double? svgSize = null)
        {
            ImageSource imageSource;
            using (MemoryStream mem = EncodedFile.ExtractLogo(sc, out ImageHelper.ImageType type))
            {
                if (type == ImageHelper.ImageType.Svg)
                {
                    if (svgSize == null)
                        imageSource = ImageHelper.SvgToBitmapImage(mem);
                    else
                        imageSource = ImageHelper.SvgToBitmapImage(mem, (double)svgSize, (double)svgSize, true);
                }
                else
                {
                    imageSource = ImageHelper.ImageToBitmapImage(mem);
                }
            }

            return new Image
            {
                StretchDirection = StretchDirection.DownOnly,
                Stretch = Stretch.Uniform,
                UseLayoutRounding = true, // To prevent blurry image rendering
                Source = imageSource
            };
        }
        
        public static MemoryStream ExtractInterfaceEncoded(Script sc, string fileName)
        {
            string section = $"EncodedFile-InterfaceEncoded-{fileName}";
            if (sc.Sections.ContainsKey(section) == false)
                throw new InvalidOperationException($"[InterfaceEncoded\\{fileName}] does not exists in [{sc.RealPath}]");

            List<string> encoded = sc.Sections[section].GetLinesOnce();
            return DecodeInMemory(encoded);
        }
        #endregion

        #region LogoExists
        public static bool LogoExists(Script sc)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            if (!sc.Sections.ContainsKey(AuthorEncoded))
                return false;

            Dictionary<string, string> fileDict = sc.Sections[AuthorEncoded].GetIniDict();
            if (!fileDict.ContainsKey("Logo"))
                return false;

            string logoName = fileDict["Logo"];
            return sc.Sections.ContainsKey(GetSectionName(AuthorEncoded, logoName));
        }
        #endregion

        #region GetFileInfo, GetFolderInfo, GetAllFilesInfo
        public static EncodedFileInfo GetFileInfo(Script sc, string dirName, string fileName, bool detail = false)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            EncodedFileInfo info = new EncodedFileInfo
            {
                DirName = dirName,
                FileName = fileName,
            };

            if (!sc.Sections.ContainsKey(dirName))
                throw new InvalidOperationException($"Directory [{dirName}] does not exist");

            Dictionary<string, string> fileDict;
            switch (sc.Sections[dirName].DataType)
            {
                case SectionDataType.IniDict:
                    fileDict = sc.Sections[dirName].GetIniDict();
                    break;
                case SectionDataType.Lines:
                    fileDict = Ini.ParseIniLinesIniStyle(sc.Sections[dirName].GetLines());
                    break;
                default:
                    throw new InternalException("Internal Logic Error at EncodedFile.GetAllFilesInfo");
            }

            if (!fileDict.ContainsKey(fileName))
                throw new InvalidOperationException("File index does not exist");

            string fileIndex = fileDict[fileName].Trim();
            (info.RawSize, info.EncodedSize) = ParseFileIndex(fileIndex);

            if (detail)
            {
                List<string> encoded = sc.Sections[GetSectionName(dirName, fileName)].GetLinesOnce();
                info.EncodeMode = GetEncodeMode(encoded);
            }

            return info;
        }

        public static EncodedFileInfo GetLogoInfo(Script sc, bool detail = false)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            EncodedFileInfo info = new EncodedFileInfo
            {
                DirName = AuthorEncoded,
            };
            
            if (!sc.Sections.ContainsKey(AuthorEncoded))
                throw new InvalidOperationException("Directory [AuthorEncoded] does not exist");

            Dictionary<string, string> fileDict = sc.Sections[AuthorEncoded].GetIniDict();

            if (!fileDict.ContainsKey("Logo"))
                throw new InvalidOperationException("Logo does not exist");

            info.FileName = fileDict["Logo"];
            if (!fileDict.ContainsKey(info.FileName))
                throw new InvalidOperationException("File index does not exist");

            string fileIndex = fileDict[info.FileName].Trim();
            (info.RawSize, info.EncodedSize) = ParseFileIndex(fileIndex);

            if (detail)
            {
                List<string> encoded = sc.Sections[GetSectionName(AuthorEncoded, info.FileName)].GetLinesOnce();
                info.EncodeMode = GetEncodeModeInMemory(encoded);
            }

            return info;
        }

        public static List<EncodedFileInfo> GetFolderInfo(Script sc, string dirName, bool detail = false)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            if (!sc.Sections.ContainsKey(dirName))
                throw new InvalidOperationException($"Directory [{dirName}] does not exist");

            Dictionary<string, string> fileDict;
            switch (sc.Sections[dirName].DataType)
            {
                case SectionDataType.IniDict:
                    fileDict = sc.Sections[dirName].GetIniDict();
                    break;
                case SectionDataType.Lines:
                    fileDict = Ini.ParseIniLinesIniStyle(sc.Sections[dirName].GetLines());
                    break;
                default:
                    throw new InternalException("Internal Logic Error at EncodedFile.GetFolderInfo");
            }

            List<EncodedFileInfo> infos = new List<EncodedFileInfo>();
            foreach (string fileName in fileDict.Keys)
            {
                EncodedFileInfo info = new EncodedFileInfo
                {
                    DirName = dirName,
                    FileName = fileName,
                };

                if (!fileDict.ContainsKey(fileName))
                    throw new InvalidOperationException("File index does not exist");

                string fileIndex = fileDict[fileName].Trim();
                (info.RawSize, info.EncodedSize) = ParseFileIndex(fileIndex);

                if (detail)
                {
                    List<string> encoded = sc.Sections[GetSectionName(dirName, fileName)].GetLinesOnce();
                    info.EncodeMode = GetEncodeMode(encoded);
                }

                infos.Add(info);
            }

            return infos;
        }

        public static Dictionary<string, List<EncodedFileInfo>> GetAllFilesInfo(Script sc, bool detail = false)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            Dictionary<string, List<EncodedFileInfo>> infoDict = new Dictionary<string, List<EncodedFileInfo>>(StringComparer.OrdinalIgnoreCase);

            if (!sc.Sections.ContainsKey(EncodedFolders))
                return infoDict;

            List<string> dirNames = Ini.FilterLines(sc.Sections[EncodedFolders].GetLines());
            int aeIdx = dirNames.FindIndex(x => x.Equals(AuthorEncoded, StringComparison.OrdinalIgnoreCase));
            if (aeIdx != -1)
            {
                App.Logger.SystemWrite(new LogInfo(LogState.Error, $"Error at script [{sc.TreePath}]\r\nSection [AuthorEncoded] should not be listed in [EncodedFolders]"));
                dirNames.RemoveAt(aeIdx);
            }

            int ieIdx = dirNames.FindIndex(x => x.Equals(InterfaceEncoded, StringComparison.OrdinalIgnoreCase));
            if (ieIdx != -1)
            {
                App.Logger.SystemWrite(new LogInfo(LogState.Error, $"Error at script [{sc.TreePath}]\r\nSection [InterfaceEncoded] should not be listed in [EncodedFolders]"));
                dirNames.RemoveAt(aeIdx);
            }

            foreach (string dirName in dirNames)
            {
                if (!infoDict.ContainsKey(dirName))
                    infoDict[dirName] = new List<EncodedFileInfo>();

                // Follow WB082 behavior
                if (!sc.Sections.ContainsKey(dirName))
                    continue;

                /*
                   Example

                   [Fonts]
                   README.txt=522,696
                   D2Coding-OFL-License.txt=2102,2803
                   D2Coding-Ver1.2-TTC-20161024.7z=3118244,4157659
                */
                Dictionary<string, string> fileDict;
                switch (sc.Sections[dirName].DataType)
                {
                    case SectionDataType.IniDict:
                        fileDict = sc.Sections[dirName].GetIniDict();
                        break;
                    case SectionDataType.Lines:
                        fileDict = Ini.ParseIniLinesIniStyle(sc.Sections[dirName].GetLines());
                        break;
                    default:
                        throw new InternalException("Internal Logic Error at EncodedFile.GetAllFilesInfo");
                }

                foreach (var kv in fileDict)
                {
                    string fileName = kv.Key;
                    string fileIndex = kv.Value;

                    EncodedFileInfo info = new EncodedFileInfo
                    {
                        DirName = dirName,
                        FileName = fileName,
                    };
                    (info.RawSize, info.EncodedSize) = ParseFileIndex(fileIndex);

                    if (detail)
                    {
                        List<string> encoded = sc.Sections[GetSectionName(dirName, fileName)].GetLinesOnce();
                        info.EncodeMode = GetEncodeMode(encoded);
                    }

                    infoDict[dirName].Add(info);
                }
            }

            return infoDict;
        }

        private static (int rawSize, int encodedSize) ParseFileIndex(string fileIndex)
        {
            Match m = Regex.Match(fileIndex, @"([0-9]+),([0-9]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            if (!m.Success)
                throw new InvalidOperationException("File index corrupted");

            if (!NumberHelper.ParseInt32(m.Groups[1].Value, out int rawSize))
                throw new InvalidOperationException("File index corrupted");
            if (!NumberHelper.ParseInt32(m.Groups[2].Value, out int encodedSize))
                throw new InvalidOperationException("File index corrupted");

            return (rawSize, encodedSize);
        }
        #endregion

        #region DeleteFile
        public static Script DeleteFile(Script sc, string dirName, string fileName, out string errorMsg)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));
            if (dirName == null)
                throw new ArgumentNullException(nameof(dirName));
            if (fileName == null)
                throw new ArgumentNullException(nameof(fileName));
            errorMsg = null;

            // Backup
            string backupFile = Path.GetTempFileName();
            File.Copy(sc.RealPath, backupFile, true);
            try
            {
                // Delete encoded file index
                Dictionary<string, string> dict = Ini.ParseIniSectionToDict(sc.RealPath, dirName);
                if (dict == null)
                {
                    errorMsg = $"Encoded folder [{dirName}] not found in [{sc.RealPath}]";
                }
                else
                {
                    if (!Ini.DeleteKey(sc.RealPath, dirName, fileName))
                        errorMsg = $"Index of encoded file [{fileName}] not found in [{sc.RealPath}]";
                }

                // Delete encoded file section
                if (!Ini.DeleteSection(sc.RealPath, GetSectionName(dirName, fileName)))
                    errorMsg = $"Encoded file [{fileName}] not found in [{sc.RealPath}]";
            }
            catch
            { // Error -> Rollback!
                File.Copy(backupFile, sc.RealPath, true);
                throw;
            }
            finally
            { // Delete backup script
                if (File.Exists(backupFile))
                    File.Delete(backupFile);
            }

            // Return refreshed script
            return sc.Project.RefreshScript(sc);
        }

        public static Script DeleteFolder(Script sc, string dirName, out string errorMsg)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));
            if (dirName == null)
                throw new ArgumentNullException(nameof(dirName));
            errorMsg = null;

            // Backup
            string backupFile = Path.GetTempFileName();
            File.Copy(sc.RealPath, backupFile, true);
            try
            {
                List<string> folders = Ini.ParseIniSection(sc.RealPath, EncodedFolders);

                // Delete index of encoded folder
                if (folders.Count(x => x.Equals(dirName, StringComparison.OrdinalIgnoreCase)) == 0)
                    errorMsg = $"Index of encoded folder [{dirName}] not found in [{sc.RealPath}]";
                if (!Ini.DeleteSection(sc.RealPath, EncodedFolders))
                    errorMsg = $"Index of encoded folder [{dirName}] not found in [{sc.RealPath}]";
                foreach (string folder in folders.Where(x => !x.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                    Ini.WriteRawLine(sc.RealPath, EncodedFolders, folder);

                Dictionary<string, string> dict = Ini.ParseIniSectionToDict(sc.RealPath, dirName);
                if (dict == null)
                {
                    errorMsg = $"Index of encoded folder [{dirName}] not found in [{sc.RealPath}]";
                }
                else
                {
                    // Get index of files
                    if (dirName.Equals(AuthorEncoded, StringComparison.OrdinalIgnoreCase))
                    {
                        if (dict.ContainsKey("Logo"))
                            dict.Remove("Logo");
                    }
                    var files = dict.Keys;

                    // Delete section [dirName]
                    if (!Ini.DeleteSection(sc.RealPath, dirName))
                        errorMsg = $"Encoded folder [{dirName}] not found in [{sc.RealPath}]";

                    // Delete encoded file section
                    foreach (string file in files)
                    {
                        if (!Ini.DeleteSection(sc.RealPath, GetSectionName(dirName, file)))
                            errorMsg = $"Encoded folder [{dirName}] not found in [{sc.RealPath}]";
                    }
                }
            }
            catch
            { // Error -> Rollback!
                File.Copy(backupFile, sc.RealPath, true);
                throw;
            }
            finally
            { // Delete backup script
                if (File.Exists(backupFile))
                    File.Delete(backupFile);
            }

            // Return refreshed script
            return sc.Project.RefreshScript(sc);
        }

        public static Script DeleteLogo(Script sc, out string errorMsg)
        {
            if (sc == null)
                throw new ArgumentNullException(nameof(sc));

            // Backup
            string backupFile = Path.GetTempFileName();
            File.Copy(sc.RealPath, backupFile, true);
            try
            {
                errorMsg = null;

                // Get filename of logo
                Dictionary<string, string> dict = Ini.ParseIniSectionToDict(sc.RealPath, AuthorEncoded);
                if (dict == null)
                {
                    errorMsg = $"Logo not found in [{sc.RealPath}]";
                    return sc;
                }

                if (!dict.ContainsKey("Logo"))
                {
                    errorMsg = $"Logo not found in [{sc.RealPath}]";
                    return sc;
                }
                    
                string logoFile = dict["Logo"];
                if (!dict.ContainsKey(logoFile))
                {
                    errorMsg = $"Logo not found in [{sc.RealPath}]";
                    return sc;
                }   

                // Delete encoded file section
                if (!Ini.DeleteSection(sc.RealPath, GetSectionName(AuthorEncoded, logoFile)))
                    errorMsg = $"Encoded file [{logoFile}] not found in [{sc.RealPath}]";

                // Delete encoded file index
                if (!(Ini.DeleteKey(sc.RealPath, AuthorEncoded, logoFile) && Ini.DeleteKey(sc.RealPath, AuthorEncoded, "Logo")))
                    errorMsg = $"Unable to delete index of logo [{logoFile}] from [{sc.RealPath}]";
            }
            catch
            { // Error -> Rollback!
                File.Copy(backupFile, sc.RealPath, true);
                throw;
            }
            finally
            { // Delete backup script
                if (File.Exists(backupFile))
                    File.Delete(backupFile);
            }

            // Return refreshed script
            return sc.Project.RefreshScript(sc);
        }
        #endregion

        #region Encode
        private static Script Encode(Script sc, string dirName, string fileName, byte[] input, EncodeMode mode, bool encodeLogo)
        {
            using (MemoryStream ms = new MemoryStream(input))
            {
                return Encode(sc, dirName, fileName, ms, mode, encodeLogo);
            }
        }

        private static Script Encode(Script sc, string dirName, string fileName, Stream inputStream, EncodeMode mode, bool encodeLogo)
        {
            byte[] fileNameUTF8 = Encoding.UTF8.GetBytes(fileName);
            if (fileNameUTF8.Length == 0 || 512 <= fileNameUTF8.Length)
                throw new InvalidOperationException("UTF8 encoded filename should be shorter than 512B");
            string section = $"EncodedFile-{dirName}-{fileName}";

            // Check Overwrite
            bool fileOverwrite = false;
            if (sc.Sections.ContainsKey(dirName))
            {
                // Check if [{dirName}] section and [EncodedFile-{dirName}-{fileName}] section exists
                ScriptSection scSect = sc.Sections[dirName];
                switch (scSect.DataType)
                {
                    case SectionDataType.IniDict:
                        if (scSect.GetIniDict().ContainsKey(fileName) &&
                            sc.Sections.ContainsKey(section))
                            fileOverwrite = true;
                        break;
                    case SectionDataType.Lines:
                        var dict = Ini.ParseIniLinesIniStyle(scSect.GetLines());
                        if (0 < dict.Count(x => x.Key.Equals(fileName, StringComparison.OrdinalIgnoreCase)) &&
                            sc.Sections.ContainsKey(section))
                            fileOverwrite = true;
                        break;
                    default:
                        throw new InternalException("Internal Logic Error at DeleteFile");
                }
            }

            int encodedLen;
            string tempFile = Path.GetTempFileName();
            List<IniKey> keys;
            try
            {
                using (FileStream encodeStream = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    // [Stage 1] Compress file with zlib
                    int readByte;
                    byte[] buffer = new byte[4096 * 1024]; // 4MB
                    Crc32Checksum crc32 = new Crc32Checksum();
                    switch (mode)
                    {
                        case EncodeMode.ZLib:
                            using (ZLibStream zs = new ZLibStream(encodeStream, CompressionMode.Compress, CompressionLevel.Level6, true))
                            {
                                while ((readByte = inputStream.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    crc32.Append(buffer, 0, readByte);
                                    zs.Write(buffer, 0, readByte);
                                }
                            }
                            break;
                        case EncodeMode.Raw:
                            while ((readByte = inputStream.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                crc32.Append(buffer, 0, readByte);
                                encodeStream.Write(buffer, 0, readByte);
                            }
                            break;
#if ENABLE_XZ
                        case EncodeMode.XZ:
                            using (XZOutputStream xzs = new XZOutputStream(encodeStream, Environment.ProcessorCount, XZOutputStream.DefaultPreset, true))
                            {
                                while ((readByte = inputStream.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    crc32.Append(buffer, 0, readByte);
                                    xzs.Write(buffer, 0, readByte);
                                }
                            }
                            break;
#endif
                        default:
                            throw new InternalException($"Wrong EncodeMode [{mode}]");
                    }
                    long compressedBodyLen = encodeStream.Position;
                    long inputLen = inputStream.Length;

                    // [Stage 2] Generate first footer
                    byte[] rawFooter = new byte[0x226]; // 0x550
                    {
                        // 0x000 - 0x1FF : Filename and its length
                        rawFooter[0] = (byte)fileNameUTF8.Length;
                        fileNameUTF8.CopyTo(rawFooter, 1);
                        for (int i = 1 + fileNameUTF8.Length; i < 0x200; i++)
                            rawFooter[i] = 0; // Null Pad
                        // 0x200 - 0x207 : 8B -> Length of raw file, in little endian
                        BitConverter.GetBytes(inputLen).CopyTo(rawFooter, 0x200);
                        switch (mode)
                        {
                            case EncodeMode.ZLib: // Type 1
#if ENABLE_XZ
                            case EncodeMode.XZ: // Type 3
#endif
                                // 0x208 - 0x20F : 8B -> Length of compressed body, in little endian
                                BitConverter.GetBytes(compressedBodyLen).CopyTo(rawFooter, 0x208);
                                // 0x210 - 0x21F : 16B -> Null padding
                                for (int i = 0x210; i < 0x220; i++)
                                    rawFooter[i] = 0;
                                break;
                            case EncodeMode.Raw: // Type 2
                                // 0x208 - 0x21F : 16B -> Null padding
                                for (int i = 0x208; i < 0x220; i++)
                                    rawFooter[i] = 0;
                                break;
                            default:
                                throw new InternalException($"Wrong EncodeMode [{mode}]");
                        }
                        // 0x220 - 0x223 : CRC32 of raw file
                        BitConverter.GetBytes(crc32.Checksum).CopyTo(rawFooter, 0x220);
                        // 0x224         : 1B -> Compress Mode (Type 1 : 00, Type 2 : 01)
                        rawFooter[0x224] = (byte)mode;
                        // 0x225         : 1B -> ZLib Compress Level (Type 1 : 01 ~ 09, Type 2 : 00)
                        switch (mode)
                        {
                            case EncodeMode.ZLib: // Type 1
                                rawFooter[0x225] = (byte)CompressionLevel.Level6;
                                break;
                            case EncodeMode.Raw: // Type 2
                                rawFooter[0x225] = 0;
                                break;
#if ENABLE_XZ
                            case EncodeMode.XZ: // Type 3
                                rawFooter[0x225] = (byte)XZOutputStream.DefaultPreset;
                                break;
#endif
                            default:
                                throw new InternalException($"Wrong EncodeMode [{mode}]");
                        }
                    }

                    // [Stage 3] Compress first footer and concat to body
                    long compressedFooterLen = encodeStream.Position;
                    using (ZLibStream zs = new ZLibStream(encodeStream, CompressionMode.Compress, CompressionLevel.Default, true))
                    {
                        zs.Write(rawFooter, 0, rawFooter.Length);
                    }
                    encodeStream.Flush();
                    compressedFooterLen = encodeStream.Position - compressedFooterLen;

                    // [Stage 4] Generate final footer
                    {
                        byte[] finalFooter = new byte[0x24];

                        // 0x00 - 0x04 : 4B -> CRC32 of compressed body and compressed footer
                        BitConverter.GetBytes(CalcCrc32(encodeStream)).CopyTo(finalFooter, 0x00);
                        // 0x04 - 0x08 : 4B -> Unknown - Always 1
                        BitConverter.GetBytes((uint)1).CopyTo(finalFooter, 0x04);
                        // 0x08 - 0x0B : 4B -> Delphi ZLBArchive Component version (Always 2)
                        BitConverter.GetBytes((uint)2).CopyTo(finalFooter, 0x08);
                        // 0x0C - 0x0F : 4B -> Zlib Compressed Footer Length
                        BitConverter.GetBytes((int)compressedFooterLen).CopyTo(finalFooter, 0x0C);
                        // 0x10 - 0x17 : 8B -> Compressed/Raw File Length
                        BitConverter.GetBytes(compressedBodyLen).CopyTo(finalFooter, 0x10);
                        // 0x18 - 0x1B : 4B -> Unknown - Always 1
                        BitConverter.GetBytes((uint)1).CopyTo(finalFooter, 0x18);
                        // 0x1C - 0x23 : 8B -> Unknown - Always 0
                        for (int i = 0x1C; i < 0x24; i++)
                            finalFooter[i] = 0;

                        encodeStream.Write(finalFooter, 0, finalFooter.Length);
                    }

                    // [Stage 5] Encode with Base64 and split into 4090B
                    encodeStream.Flush();
                    (keys, encodedLen) = SplitBase64.Encode(encodeStream, section);
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }

            // [Stage 6] Before writing to file, backup original script
            string backupFile = Path.GetTempFileName();
            File.Copy(sc.RealPath, backupFile, true);

            // [Stage 7] Write to file
            try
            {
                // Write folder info to [EncodedFolders]
                if (!encodeLogo)
                { // "AuthorEncoded" and "InterfaceEncoded" should not be listed here
                    bool writeFolderSection = true;
                    if (sc.Sections.ContainsKey("EncodedFolders"))
                    {
                        List<string> folders = sc.Sections["EncodedFolders"].GetLines();
                        if (0 < folders.Count(x => x.Equals(dirName, StringComparison.OrdinalIgnoreCase)))
                            writeFolderSection = false;
                    }

                    if (writeFolderSection)
                        Ini.WriteRawLine(sc.RealPath, "EncodedFolders", dirName, false);
                }

                // Write file info into [{dirName}]
                Ini.WriteKey(sc.RealPath, dirName, fileName, $"{inputStream.Length},{encodedLen}"); // UncompressedSize,EncodedSize

                // Write encoded file into [EncodedFile-{dirName}-{fileName}]
                if (fileOverwrite)
                    Ini.DeleteSection(sc.RealPath, section); // Delete existing encoded file
                Ini.WriteKeys(sc.RealPath, keys);

                // Write additional line when encoding logo.
                if (encodeLogo)
                {
                    string lastLogo = Ini.ReadKey(sc.RealPath, "AuthorEncoded", "Logo");
                    Ini.WriteKey(sc.RealPath, "AuthorEncoded", "Logo", fileName);

                    if (lastLogo != null)
                    {
                        Ini.DeleteKey(sc.RealPath, "AuthorEncoded", lastLogo);
                        Ini.DeleteSection(sc.RealPath, $"EncodedFile-AuthorEncoded-{lastLogo}");
                    }
                }    
            }
            catch
            { // Error -> Rollback!
                File.Copy(backupFile, sc.RealPath, true);
                throw new InvalidOperationException($"Error while writing encoded file into [{sc.RealPath}]");
            }
            finally
            { // Delete backup script
                if (File.Exists(backupFile))
                    File.Delete(backupFile);
            }
            
            // [Stage 8] Refresh Script
            return sc.Project.RefreshScript(sc);
        }
        #endregion

        #region Decode
        private static long Decode(List<string> encodedList, Stream outStream)
        {
            string tempDecode = Path.GetTempFileName();
            string tempComp = Path.GetTempFileName();
            try
            {
                using (FileStream decodeStream = new FileStream(tempDecode, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    // [Stage 1] Concat sliced base64-encoded lines into one string
                    int decodeLen = SplitBase64.Decode(encodedList, decodeStream);

                    // [Stage 2] Read final footer
                    const int finalFooterLen = 0x24;
                    byte[] finalFooter = new byte[finalFooterLen];
                    int finalFooterIdx = decodeLen - finalFooterLen;

                    decodeStream.Flush();
                    decodeStream.Position = finalFooterIdx;
                    int readByte = decodeStream.Read(finalFooter, 0, finalFooterLen);
                    Debug.Assert(readByte == finalFooterLen);

                    // 0x00 - 0x04 : 4B -> CRC32
                    uint full_crc32 = BitConverter.ToUInt32(finalFooter, 0x00);
                    // 0x0C - 0x0F : 4B -> Zlib Compressed Footer Length
                    int compressedFooterLen = (int)BitConverter.ToUInt32(finalFooter, 0x0C);
                    int compressedFooterIdx = finalFooterIdx - compressedFooterLen;
                    // 0x10 - 0x17 : 8B -> Zlib Compressed File Length
                    int compressedBodyLen = (int)BitConverter.ToUInt64(finalFooter, 0x10);

                    // [Stage 3] Validate final footer
                    if (compressedBodyLen != compressedFooterIdx)
                        throw new InvalidOperationException("Encoded file is corrupted: finalFooter");
                    if (full_crc32 != CalcCrc32(decodeStream, 0, finalFooterIdx))
                        throw new InvalidOperationException("Encoded file is corrupted: finalFooter");

                    // [Stage 4] Decompress first footer
                    byte[] firstFooter = new byte[0x226];
                    using (MemoryStream compressedFooter = new MemoryStream(compressedFooterLen))
                    { 
                        decodeStream.Position = compressedFooterIdx;
                        decodeStream.CopyTo(compressedFooter, compressedFooterLen);
                        decodeStream.Position = 0;

                        compressedFooter.Flush();
                        compressedFooter.Position = 0;
                        using (ZLibStream zs = new ZLibStream(compressedFooter, CompressionMode.Decompress, CompressionLevel.Default))
                        {
                            readByte = zs.Read(firstFooter, 0, firstFooter.Length);
                            Debug.Assert(readByte == firstFooter.Length);
                        }
                    }

                    // [Stage 5] Read first footer
                    // 0x200 - 0x207 : 8B -> Length of raw file, in little endian
                    int rawBodyLen = BitConverter.ToInt32(firstFooter, 0x200);
                    // 0x208 - 0x20F : 8B -> Length of zlib-compressed file, in little endian
                    //     Note: In Type 2, 0x208 entry is null - padded
                    int compressedBodyLen2 = BitConverter.ToInt32(firstFooter, 0x208);
                    // 0x220 - 0x223 : 4B -> CRC32C Checksum of zlib-compressed file
                    uint compressedBody_crc32 = BitConverter.ToUInt32(firstFooter, 0x220);
                    // 0x224         : 1B -> Compress Mode (Type 1 : 00, Type 2 : 01)
                    byte compMode = firstFooter[0x224];
                    // 0x225         : 1B -> ZLib Compress Level (Type 1 : 01~09, Type 2 : 00)
                    byte compLevel = firstFooter[0x225];

                    // [Stage 6] Validate first footer
                    switch ((EncodeMode)compMode)
                    {
                        case EncodeMode.ZLib: // Type 1, zlib
                            if (compressedBodyLen2 == 0 || 
                                compressedBodyLen2 != compressedBodyLen)
                                throw new InvalidOperationException("Encoded file is corrupted: compMode");
                            if (compLevel < 1 || 9 < compLevel)
                                throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                            break;
                        case EncodeMode.Raw: // Type 2, raw
                            if (compressedBodyLen2 != 0)
                                throw new InvalidOperationException("Encoded file is corrupted: compMode");
                            if (compLevel != 0)
                                throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                            break;
#if ENABLE_XZ
                        case EncodeMode.XZ: // Type 3, LZMA
                            if (compressedBodyLen2 == 0 || (compressedBodyLen2 != compressedBodyLen))
                                throw new FileDecodeFailException("Encoded file is corrupted: compMode");
                            if (compLevel < 1 || 9 < compLevel)
                                throw new FileDecodeFailException("Encoded file is corrupted: compLevel");
                            break;
#endif
                        default:
                            throw new InvalidOperationException("Encoded file is corrupted: compMode");
                    }

                    // [Stage 7] Decompress body
                    Crc32Checksum crc32 = new Crc32Checksum();
                    long outPosBak = outStream.Position;
                    byte[] buffer = new byte[4096 * 1024]; // 4MB
                    switch ((EncodeMode)compMode)
                    {
                        case EncodeMode.ZLib: // Type 1, zlib
                            using (FileStream compStream = new FileStream(tempComp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                            {
                                decodeStream.Position = 0;
                                decodeStream.CopyTo(compStream, compressedBodyLen);

                                compStream.Flush();
                                compStream.Position = 0;
                                using (ZLibStream zs = new ZLibStream(compStream, CompressionMode.Decompress, true))
                                {
                                    while ((readByte = zs.Read(buffer, 0, buffer.Length)) != 0)
                                    {
                                        crc32.Append(buffer, 0, readByte);
                                        outStream.Write(buffer, 0, readByte);
                                    }
                                }
                            }
                            break;
                        case EncodeMode.Raw: // Type 2, raw
                            {
                                decodeStream.Position = 0;

                                int offset = 0;
                                while (offset < rawBodyLen)
                                {
                                    if (offset + buffer.Length < rawBodyLen)
                                        readByte = decodeStream.Read(buffer, 0, buffer.Length);
                                    else
                                        readByte = decodeStream.Read(buffer, 0, rawBodyLen - offset);

                                    crc32.Append(buffer, 0, readByte);
                                    outStream.Write(buffer, 0, readByte);
                                    
                                    offset += readByte;
                                }
                            }
                            break;
#if ENABLE_XZ
                        case EncodeMode.XZ: // Type 3, LZMA
                            using (FileStream compStream = new FileStream(tempComp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                            {
                                decodeStream.Position = 0;
                                decodeStream.CopyTo(compStream, compressedBodyLen);

                                compStream.Flush();
                                compStream.Position = 0;
                                using (XZInputStream xzs = new XZInputStream(compStream, true))
                                {
                                    while ((readByte = xzs.Read(buffer, 0, buffer.Length)) != 0)
                                    {
                                        crc32.Append(buffer, 0, readByte);
                                        outStream.Write(buffer, 0, readByte);
                                    }
                                }
                            }
                            break;
#endif
                        default:
                            throw new InvalidOperationException("Encoded file is corrupted: compMode");
                    }
                    long outLen = outStream.Position - outPosBak;

                    // [Stage 8] Validate decompressed body
                    if (compressedBody_crc32 != crc32.Checksum)
                        throw new InvalidOperationException("Encoded file is corrupted: body");

                    return outLen;
                }
            }
            finally
            {
                if (!File.Exists(tempDecode))
                    File.Delete(tempDecode);
                if (!File.Exists(tempComp))
                    File.Delete(tempComp);
            }
        }
        #endregion

        #region DecodeInMemory
        private static MemoryStream DecodeInMemory(List<string> encodedList)
        {
            // [Stage 1] Concat sliced base64-encoded lines into one string
            byte[] decoded = SplitBase64.DecodeInMemory(encodedList);

            // [Stage 2] Read final footer
            const int finalFooterLen = 0x24;
            int finalFooterIdx = decoded.Length - finalFooterLen;
            // 0x00 - 0x04 : 4B -> CRC32
            uint full_crc32 = BitConverter.ToUInt32(decoded, finalFooterIdx + 0x00);
            // 0x0C - 0x0F : 4B -> Zlib Compressed Footer Length
            int compressedFooterLen = (int)BitConverter.ToUInt32(decoded, finalFooterIdx + 0x0C);
            int compressedFooterIdx = decoded.Length - (finalFooterLen + compressedFooterLen);
            // 0x10 - 0x17 : 8B -> Zlib Compressed File Length
            int compressedBodyLen = (int)BitConverter.ToUInt64(decoded, finalFooterIdx + 0x10);

            // [Stage 3] Validate final footer
            if (compressedBodyLen != compressedFooterIdx)
                throw new InvalidOperationException("Encoded file is corrupted: finalFooter");
            uint calcFull_crc32 = Crc32Checksum.Crc32(decoded, 0, finalFooterIdx);
            if (full_crc32 != calcFull_crc32)
                throw new InvalidOperationException("Encoded file is corrupted: finalFooter");

            // [Stage 4] Decompress first footer
            byte[] rawFooter;
            using (MemoryStream rawFooterStream = new MemoryStream())
            {
                using (MemoryStream ms = new MemoryStream(decoded, compressedFooterIdx, compressedFooterLen))
                using (ZLibStream zs = new ZLibStream(ms, CompressionMode.Decompress, CompressionLevel.Default))
                {
                    zs.CopyTo(rawFooterStream);
                }

                rawFooter = rawFooterStream.ToArray();
            }

            // [Stage 5] Read first footer
            // 0x200 - 0x207 : 8B -> Length of raw file, in little endian
            int rawBodyLen = BitConverter.ToInt32(rawFooter, 0x200);
            // 0x208 - 0x20F : 8B -> Length of zlib-compressed file, in little endian
            //     Note: In Type 2, 0x208 entry is null - padded
            int compressedBodyLen2 = BitConverter.ToInt32(rawFooter, 0x208);
            // 0x220 - 0x223 : 4B -> CRC32C Checksum of zlib-compressed file
            uint compressedBody_crc32 = BitConverter.ToUInt32(rawFooter, 0x220);
            // 0x224         : 1B -> Compress Mode (Type 1 : 00, Type 2 : 01)
            byte compMode = rawFooter[0x224];
            // 0x225         : 1B -> ZLib Compress Level (Type 1 : 01~09, Type 2 : 00)
            byte compLevel = rawFooter[0x225];

            // [Stage 6] Validate first footer
            switch ((EncodeMode)compMode)
            {
                case EncodeMode.ZLib: // Type 1, zlib
                    {
                        if (compressedBodyLen2 == 0 || 
                            compressedBodyLen2 != compressedBodyLen)
                            throw new InvalidOperationException("Encoded file is corrupted: compMode");
                        if (compLevel < 1 || 9 < compLevel)
                            throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                    }
                    break;
                case EncodeMode.Raw: // Type 2, raw
                    {
                        if (compressedBodyLen2 != 0)
                            throw new InvalidOperationException("Encoded file is corrupted: compMode");
                        if (compLevel != 0)
                            throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                    }
                    break;
#if ENABLE_XZ
                case EncodeMode.XZ: // Type 3, LZMA
                    {
                        if (compressedBodyLen2 == 0 || (compressedBodyLen2 != compressedBodyLen))
                            throw new FileDecodeFailException($"Encoded file is corrupted: compMode");
                        if (compLevel < 1 || 9 < compLevel)
                            throw new FileDecodeFailException($"Encoded file is corrupted: compLevel");
                    }
                    break;
#endif
                default:
                    throw new InvalidOperationException("Encoded file is corrupted: compMode");
            }

            // [Stage 7] Decompress body
            MemoryStream rawBodyStream = new MemoryStream(); // This stream should be alive even after this method returns
            switch ((EncodeMode)compMode)
            {
                case EncodeMode.ZLib: // Type 1, zlib
                    {
                        using (MemoryStream ms = new MemoryStream(decoded, 0, compressedBodyLen))
                        using (ZLibStream zs = new ZLibStream(ms, CompressionMode.Decompress, false))
                        {
                            zs.CopyTo(rawBodyStream);
                        }
                    }
                    break;
                case EncodeMode.Raw: // Type 2, raw
                    {
                        rawBodyStream.Write(decoded, 0, rawBodyLen);
                    }
                    break;
#if ENABLE_XZ
                case EncodeMode.XZ: // Type 3, LZMA
                    {
                        using (MemoryStream ms = new MemoryStream(decoded, 0, compressedBodyLen))
                        using (XZInputStream xzs = new XZInputStream(ms))
                        {
                            xzs.CopyTo(rawBodyStream);
                        }
                    }
                    break;
#endif
                default:
                    throw new InvalidOperationException("Encoded file is corrupted: compMode");
            }

            rawBodyStream.Position = 0;

            // [Stage 8] Validate decompressed body
            uint calcCompBody_crc32 = Crc32Checksum.Crc32(rawBodyStream.ToArray());
            if (compressedBody_crc32 != calcCompBody_crc32)
                throw new InvalidOperationException("Encoded file is corrupted: body");

            // [Stage 9] Return decompressed body stream
            rawBodyStream.Position = 0;
            return rawBodyStream;
        }
        #endregion

        #region GetEncodeMode
        private static EncodeMode GetEncodeMode(List<string> encodedList)
        {
            string tempDecode = Path.GetTempFileName();
            try
            {
                using (FileStream decodeStream = new FileStream(tempDecode, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    // [Stage 1] Concat sliced base64-encoded lines into one string
                    int decodeLen = SplitBase64.Decode(encodedList, decodeStream);

                    // [Stage 2] Read final footer
                    const int finalFooterLen = 0x24;
                    byte[] finalFooter = new byte[finalFooterLen];
                    int finalFooterIdx = decodeLen - finalFooterLen;

                    decodeStream.Flush();
                    decodeStream.Position = finalFooterIdx;
                    int readByte = decodeStream.Read(finalFooter, 0, finalFooterLen);
                    Debug.Assert(readByte == finalFooterLen);

                    // 0x00 - 0x04 : 4B -> CRC32
                    uint full_crc32 = BitConverter.ToUInt32(finalFooter, 0x00);
                    // 0x0C - 0x0F : 4B -> Zlib Compressed Footer Length
                    int compressedFooterLen = (int)BitConverter.ToUInt32(finalFooter, 0x0C);
                    int compressedFooterIdx = finalFooterIdx - compressedFooterLen;
                    // 0x10 - 0x17 : 8B -> Zlib Compressed File Length
                    int compressedBodyLen = (int)BitConverter.ToUInt64(finalFooter, 0x10);

                    // [Stage 3] Validate final footer
                    if (compressedBodyLen != compressedFooterIdx)
                        throw new InvalidOperationException("Encoded file is corrupted: finalFooter");
                    if (full_crc32 != CalcCrc32(decodeStream, 0, finalFooterIdx))
                        throw new InvalidOperationException("Encoded file is corrupted: finalFooter");

                    // [Stage 4] Decompress first footer
                    byte[] firstFooter = new byte[0x226];
                    using (MemoryStream compressedFooter = new MemoryStream(compressedFooterLen))
                    {
                        decodeStream.Position = compressedFooterIdx;
                        decodeStream.CopyTo(compressedFooter, compressedFooterLen);
                        decodeStream.Position = 0;

                        compressedFooter.Flush();
                        compressedFooter.Position = 0;
                        using (ZLibStream zs = new ZLibStream(compressedFooter, CompressionMode.Decompress, CompressionLevel.Default))
                        {
                            readByte = zs.Read(firstFooter, 0, firstFooter.Length);
                            Debug.Assert(readByte == firstFooter.Length);
                        }
                    }

                    // [Stage 5] Read first footer
                    // 0x224         : 1B -> Compress Mode (Type 1 : 00, Type 2 : 01)
                    byte compMode = firstFooter[0x224];
                    // 0x225         : 1B -> ZLib Compress Level (Type 1 : 01~09, Type 2 : 00)
                    byte compLevel = firstFooter[0x225];

                    // [Stage 6] Validate first footer
                    switch ((EncodeMode)compMode)
                    {
                        case EncodeMode.ZLib: // Type 1, zlib
                            if (compLevel < 1 || 9 < compLevel)
                                throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                            break;
                        case EncodeMode.Raw: // Type 2, raw
                            if (compLevel != 0)
                                throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                            break;
#if ENABLE_XZ
                        case EncodeMode.XZ: // Type 3, LZMA
                            if (compLevel < 1 || 9 < compLevel)
                                throw new FileDecodeFailException("Encoded file is corrupted: compLevel");
                            break;
#endif
                        default:
                            throw new InvalidOperationException("Encoded file is corrupted: compMode");
                    }

                    return (EncodeMode)compMode;
                }
            }
            finally
            {
                if (!File.Exists(tempDecode))
                    File.Delete(tempDecode);
            }
        }
        #endregion

        #region GetEncodeModeInMemory
        private static EncodeMode GetEncodeModeInMemory(List<string> encodedList)
        {
            // [Stage 1] Concat sliced base64-encoded lines into one string
            byte[] decoded = SplitBase64.DecodeInMemory(encodedList);

            // [Stage 2] Read final footer
            const int finalFooterLen = 0x24;
            int finalFooterIdx = decoded.Length - finalFooterLen;
            // 0x00 - 0x04 : 4B -> CRC32
            uint full_crc32 = BitConverter.ToUInt32(decoded, finalFooterIdx + 0x00);
            // 0x0C - 0x0F : 4B -> Zlib Compressed Footer Length
            int compressedFooterLen = (int)BitConverter.ToUInt32(decoded, finalFooterIdx + 0x0C);
            int compressedFooterIdx = decoded.Length - (finalFooterLen + compressedFooterLen);
            // 0x10 - 0x17 : 8B -> Zlib Compressed File Length
            int compressedBodyLen = (int)BitConverter.ToUInt64(decoded, finalFooterIdx + 0x10);

            // [Stage 3] Validate final footer
            if (compressedBodyLen != compressedFooterIdx)
                throw new InvalidOperationException("Encoded file is corrupted: finalFooter");
            uint calcFull_crc32 = Crc32Checksum.Crc32(decoded, 0, finalFooterIdx);
            if (full_crc32 != calcFull_crc32)
                throw new InvalidOperationException("Encoded file is corrupted: finalFooter");

            // [Stage 4] Decompress first footer
            byte[] rawFooter;
            using (MemoryStream rawFooterStream = new MemoryStream())
            {
                using (MemoryStream ms = new MemoryStream(decoded, compressedFooterIdx, compressedFooterLen))
                using (ZLibStream zs = new ZLibStream(ms, CompressionMode.Decompress, CompressionLevel.Default))
                {
                    zs.CopyTo(rawFooterStream);
                }

                rawFooter = rawFooterStream.ToArray();
            }

            // [Stage 5] Read first footer
            // 0x224         : 1B -> Compress Mode (Type 1 : 00, Type 2 : 01)
            byte compMode = rawFooter[0x224];
            // 0x225         : 1B -> ZLib Compress Level (Type 1 : 01~09, Type 2 : 00)
            byte compLevel = rawFooter[0x225];

            // [Stage 6] Validate first footer
            switch ((EncodeMode)compMode)
            {
                case EncodeMode.ZLib: // Type 1, zlib
                    if (compLevel < 1 || 9 < compLevel)
                        throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                    break;
                case EncodeMode.Raw: // Type 2, raw
                    if (compLevel != 0)
                        throw new InvalidOperationException("Encoded file is corrupted: compLevel");
                    break;
#if ENABLE_XZ
                case EncodeMode.XZ: // Type 3, LZMA
                    if (compLevel < 1 || 9 < compLevel)
                        throw new FileDecodeFailException("Encoded file is corrupted: compLevel");
                    break;
#endif
                default:
                    throw new InvalidOperationException("Encoded file is corrupted: compMode");
            }

            return (EncodeMode)compMode;
        }
        #endregion

        #region Utility
        private static uint CalcCrc32(Stream stream)
        {
            long posBak = stream.Position;
            stream.Position = 0;

            Crc32Checksum calc = new Crc32Checksum();
            byte[] buffer = new byte[4096 * 1024]; // 4MB
            while (stream.Position < stream.Length)
            {
                int readByte = stream.Read(buffer, 0, buffer.Length);
                calc.Append(buffer, 0, readByte);
            }
            
            stream.Position = posBak;
            return calc.Checksum;
        }

        private static uint CalcCrc32(Stream stream, int startOffset, int length)
        {
            if (stream.Length <= startOffset)
                throw new ArgumentOutOfRangeException(nameof(startOffset));
            if (stream.Length <= startOffset + length)
                throw new ArgumentOutOfRangeException(nameof(length));

            long posBak = stream.Position;
            stream.Position = startOffset;

            int offset = startOffset;
            Crc32Checksum calc = new Crc32Checksum();
            byte[] buffer = new byte[4096 * 1024]; // 4MB
            while (offset < startOffset + length)
            {
                int readByte = stream.Read(buffer, 0, buffer.Length);
                if (offset + readByte < startOffset + length)
                    calc.Append(buffer, 0, readByte);
                else
                    calc.Append(buffer, 0, startOffset + length - offset);
                offset += readByte;
            }

            stream.Position = posBak;
            return calc.Checksum;
        }
        #endregion
    }
    #endregion

    #region SplitBase64
    public static class SplitBase64
    {
        #region Encode
        public static (List<IniKey>, int) Encode(Stream stream, string section)
        {
            int idx = 0;
            int encodedLen = 0;
            List<IniKey> keys = new List<IniKey>((int)(stream.Length * 4 / 3) / 4090 + 1);

            long posBak = stream.Position;
            stream.Position = 0;

            byte[] buffer = new byte[4090 * 1024 * 3]; // Process ~12MB at once (encode to ~16MB)
            while (stream.Position < stream.Length)
            {
                int readByte = stream.Read(buffer, 0, buffer.Length);
                string encodedStr = Convert.ToBase64String(buffer, 0, readByte);

                // Count Base64 string length
                encodedLen += encodedStr.Length;

                // Remove Base64 Padding (==, =)
                if (readByte < buffer.Length)
                    encodedStr = encodedStr.TrimEnd('=');
                    
                // Tokenize encoded string by 4090 chars
                int encodeLine = encodedStr.Length / 4090;
                for (int x = 0; x < encodeLine; x++)
                {
                    keys.Add(new IniKey(section, idx.ToString(), encodedStr.Substring(x * 4090, 4090)));
                    idx += 1;
                }

                string lastLine = encodedStr.Substring(encodeLine * 4090);
                if (0 < lastLine.Length && encodeLine < 1024 * 4)
                    keys.Add(new IniKey(section, idx.ToString(), lastLine));
            }

            stream.Position = posBak;

            keys.Insert(0, new IniKey(section, "lines", idx.ToString())); // lines=X
            return (keys, encodedLen);
        }
        #endregion

        #region Decode
        public static int Decode(List<string> encodedList, Stream outStream)
        {
            // Remove "lines=n"
            encodedList.RemoveAt(0);

            if (Ini.GetKeyValueFromLines(encodedList, out List<string> keys, out List<string> base64Blocks))
                throw new InvalidOperationException("Encoded lines are malformed");
            if (!keys.All(StringHelper.IsInteger))
                throw new InvalidOperationException("Key of the encoded lines are malformed");

            if (base64Blocks.Count == 0)
                throw new InvalidOperationException("Encoded lines are not found");
            int lineLen = base64Blocks[0].Length;

            int encodeLen = 0;
            int decodeLen = 0;
            StringBuilder b = new StringBuilder(4090 * 4096); // Process encoded block ~16MB at once
            for (int i = 0; i < base64Blocks.Count; i++)
            {
                string block = base64Blocks[i];

                if (4090 < block.Length || 
                    i + 1 < base64Blocks.Count && block.Length != lineLen)
                    throw new InvalidOperationException("Length of encoded lines is inconsistent");

                b.Append(block);
                encodeLen += block.Length;

                // If buffer is full, decode ~16MB to ~12MB raw bytes
                if ((i + 1) % 4096 == 0)
                {
                    byte[] buffer = Convert.FromBase64String(b.ToString());
                    outStream.Write(buffer, 0, buffer.Length);
                    decodeLen += buffer.Length;
                    b.Clear();
                }
            }

            // Append = padding
            switch (encodeLen % 4)
            {
                case 0:
                    break;
                case 1:
                    throw new InvalidOperationException("Wrong base64 padding");
                case 2:
                    b.Append("==");
                    break;
                case 3:
                    b.Append("=");
                    break;
            }

            byte[] finalBuffer = Convert.FromBase64String(b.ToString());
            decodeLen += finalBuffer.Length;
            outStream.Write(finalBuffer, 0, finalBuffer.Length);

            return decodeLen;
        }
        #endregion

        #region DecodeInMemory
        public static byte[] DecodeInMemory(List<string> encodedList)
        {
            // Remove "lines=n"
            encodedList.RemoveAt(0);         
           
            if (Ini.GetKeyValueFromLines(encodedList, out List<string> keys, out List<string> base64Blocks))
                throw new InvalidOperationException("Encoded lines are malformed");
            if (!keys.All(StringHelper.IsInteger))
                throw new InvalidOperationException("Key of the encoded lines are malformed");
            if (base64Blocks.Count == 0)
                throw new InvalidOperationException("Encoded lines are not found");

            StringBuilder b = new StringBuilder();
            foreach (string block in base64Blocks)
                b.Append(block);
            switch (b.Length % 4)
            {
                case 0:
                    break;
                case 1:
                    throw new InvalidOperationException("Encoded lines are malformed");
                case 2:
                    b.Append("==");
                    break;
                case 3:
                    b.Append("=");
                    break;
            }

            return Convert.FromBase64String(b.ToString());
        }
        #endregion
    }
    #endregion

    #region EncodedFileInfo
    public class EncodedFileInfo
    {
        public string DirName;
        public string FileName;
        public int RawSize;
        public int EncodedSize;
        public EncodedFile.EncodeMode? EncodeMode;
    }
    #endregion
}
