﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BakeryEngine
{
    using StringDictionary = Dictionary<string, string>;

    /// <summary>
    /// Contains static helper methods.
    /// </summary>
    public static class Helper
    {
        /// <summary>
        /// Count occurrences of strings.
        /// http://www.dotnetperls.com/string-occurrence
        /// </summary>
        public static int CountStringOccurrences(string text, string pattern)
        {
            // Loop through all instances of the string 'text'.
            int count = 0;
            int i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1)
            {
                i += pattern.Length;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Detect text file's encoding with BOM
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public static Encoding DetectTextEncoding(string fileName)
        {
            byte[] bom = new byte[4];
            FileStream fs = null;

            try
            {
                fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Read(bom, 0, bom.Length);
                fs.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine("[ERR]\n{0}", e.ToString());
            }

            if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return Encoding.UTF8;
            else if (bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode;
            else if (bom[0] == 0xFE && bom[1] == 0xFF)
                return Encoding.BigEndianUnicode;
            return Encoding.Default;
        }

        /// <summary>
        /// Exception used in BakeryEngine::ParseCommand
        /// </summary>
        public class UnsupportedEncodingException : Exception
        {
            public UnsupportedEncodingException() { }
            public UnsupportedEncodingException(string message) : base(message) { }
            public UnsupportedEncodingException(string message, Exception inner) : base(message, inner) { }
        }

        /// <summary>
        /// Write Unicode BOM into text file stream
        /// </summary>
        /// <param name="fs"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public static FileStream WriteTextBOM(FileStream fs, Encoding encoding)
        {
            try
            {
                if (encoding == Encoding.UTF8)
                {
                    byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
                    fs.Write(bom, 0, bom.Length);
                }
                else if (encoding == Encoding.Unicode)
                {
                    byte[] bom = new byte[] { 0xFF, 0xFE };
                    fs.Write(bom, 0, bom.Length);
                }
                else if (encoding == Encoding.BigEndianUnicode)
                {
                    byte[] bom = new byte[] { 0xFE, 0xFF };
                    fs.Write(bom, 0, bom.Length);
                }
                else if (encoding != Encoding.Default)
                { // Unsupported Encoding
                    throw new UnsupportedEncodingException(encoding.ToString() + " is not supported");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[ERR]\n{0}", e.ToString());
            }

            return fs;
        }

        /// <summary>
        /// Read full text from file.
        /// Automatically detect encoding.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public static string ReadTextFile(string fileName)
        {
            FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            char[] buffer = new char[fs.Length];
            StreamReader sr = new StreamReader(fs, Helper.DetectTextEncoding(fileName));
            sr.Read(buffer, 0, buffer.Length);
            sr.Close();
            fs.Close();
            return new string(buffer);
        }

        /// <summary>
        /// return Compile DateTime
        /// </summary>
        /// <returns></returns>
        /// Add to pre-build event : echo %date% %time% > "$(ProjectDir)\Resources\BuildDate.txt"
        /// Add to "Resources\BuildData.txt" as resources
        /// http://stackoverflow.com/questions/1600962/displaying-the-build-date
        public static DateTime GetBuildDate()
        {
            // Ex) 2016-08-30  7:10:00.25 
            // Ex) 2016-09-02  0:25:11.65 
            string[] rawBuildDateStr = Properties.Resources.BuildDate.Split(new char[] { ' ', '.', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string buildDateStr = string.Format("{0} {1}", rawBuildDateStr[0], rawBuildDateStr[1]);
            DateTime buildDate;
            try
            {
                buildDate = DateTime.ParseExact(buildDateStr, "yyyy-MM-dd H:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            { // Format Error, just print 0001-01-01
                buildDate = new DateTime(1, 1, 1);
            }
            
            return buildDate;
        }

        /// <summary>
        /// Read program's version from manifest
        /// </summary>
        /// <returns></returns>
        public static Version GetProgramVersion()
        {
            // Assembly assembly = Assembly.GetExecutingAssembly();
            // FileVersionInfo fileVerInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
            // return new Version(fileVerInfo.FileMajorPart, fileVerInfo.FileMinorPart, fileVerInfo.FileBuildPart, fileVerInfo.FilePrivatePart);
            return Assembly.GetExecutingAssembly().GetName().Version;
        }

        public static string GetProgramPath()
        {
            return Helper.RemoveLastDirChar(AppDomain.CurrentDomain.BaseDirectory);
        }
        /// <summary>
        /// Remove last \ in the path.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string RemoveLastDirChar(string path)
        {
            if (Path.GetDirectoryName(path) != null)
                path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return path;
        }

        /// <summary>
        /// Remove last newline in the string.
        /// Also removes whitespace.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static string RemoveLastNewLine(string str)
        {
            return str.Trim().TrimEnd(Environment.NewLine.ToCharArray()).Trim();
        }

        

        /// <summary>
        /// Extends Path.GetDirectoryName().
        /// If returned dir path is empty, change it to "."
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string GetDirNameEx(string path)
        {
            string dirName = Path.GetDirectoryName(path);
            if (dirName == string.Empty)
                dirName = ".";
            return dirName;
        }

        /// <summary>
        /// Parse INI style string into dictionary
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
        public static StringDictionary ParseIniStyle(string[] lines)
        {
            StringDictionary dict = new StringDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                try
                {
                    MatchCollection matches = Regex.Matches(line, @"^(.+)=(.+)$", RegexOptions.Compiled);

                    // Make instances of sections
                    for (int i = 0; i < matches.Count; i++)
                    {
                        string key = matches[i].Groups[1].Value.Trim();
                        string value = matches[i].Groups[2].Value.Trim();
                        dict[key] = value;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Concat(e.GetType(), ": ", Helper.RemoveLastNewLine(e.Message)));
                }                
            }
            return dict;
        }

        /// <summary>
        /// Parse INI style Variables string into dictionary
        /// </summary>
        /// There in format of %VarKey%=VarValue
        /// <param name="lines"></param>
        /// <returns></returns>
        public static StringDictionary ParseVarStyle(string[] lines)
        {
            StringDictionary dict = new StringDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                try
                {
                    MatchCollection matches = Regex.Matches(line, @"^%(.+)%=(.+)$", RegexOptions.Compiled);

                    // Make instances of sections
                    for (int i = 0; i < matches.Count; i++)
                    {
                        string key = matches[i].Groups[1].Value.Trim();
                        string value = matches[i].Groups[2].Value.Trim();
                        dict[key] = value;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Concat(e.GetType(), ": ", Helper.RemoveLastNewLine(e.Message)));
                }
            }
            return dict;
        }
    }
}