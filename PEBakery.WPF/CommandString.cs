﻿using PEBakery.Core;
using PEBakery.Exceptions;
using PEBakery.Helper;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PEBakery.Core
{
    public static class CommandString
    {
        const long PB = 1024L * 1024L * 1024L * 1024L * 1024L;
        const long TB = 1024L * 1024L * 1024L * 1024L;
        const long GB = 1024L * 1024L * 1024L;
        const long MB = 1024L * 1024L;
        const long KB = 1024L;

        public static List<LogInfo> StrFormat(EngineState s, CodeCommand cmd)
        {
            List<LogInfo> logs = new List<LogInfo>();

            CodeInfo_StrFormat info = cmd.Info as CodeInfo_StrFormat;
            if (info == null)
                throw new InternalCodeInfoException();

            StrFormatType type = info.Type;
            try
            {
                switch (type)
                {
                    case StrFormatType.Bytes:
                        {
                            StrFormatInfo_Bytes subInfo = info.SubInfo as StrFormatInfo_Bytes;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string byteSizeStr = StringEscaper.Preprocess(s, subInfo.ByteSize);
                            if (long.TryParse(byteSizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long byteSize) == false)
                                throw new ExecuteErrorException($"[{byteSizeStr}] is not valid integer");

                            if (byteSize < 0)
                                throw new ExecuteErrorException($"[{byteSize}] must be positive integer");

                            string destStr = string.Empty;
                            if (PB <= byteSize)
                                destStr = $"{((decimal)byteSize / PB):0.###}PB";
                            else if (TB <= byteSize)
                                destStr = $"{((decimal)byteSize / TB):0.###}TB";
                            else if (GB <= byteSize)
                                destStr = $"{((decimal)byteSize / GB):0.###}GB";
                            else if (MB <= byteSize)
                                destStr = $"{((decimal)byteSize / MB):0.###}MB";
                            else
                                destStr = $"{((decimal)byteSize / KB):0.###}KB";

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Ceil:
                    case StrFormatType.Floor:
                    case StrFormatType.Round:
                        {
                            StrFormatInfo_CeilFloorRound subInfo = info.SubInfo as StrFormatInfo_CeilFloorRound;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            // subInfo.SizeVar;
                            string roundToStr = StringEscaper.Preprocess(s, subInfo.RoundTo);
                            // Is roundToStr number?
                            if (long.TryParse(roundToStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long roundTo) == false)
                            { // Is roundToStr is one of K, M, G, T, P?
                                if (roundToStr.Equals("K", StringComparison.OrdinalIgnoreCase))
                                    roundTo = KB;
                                else if (roundToStr.Equals("M", StringComparison.OrdinalIgnoreCase))
                                    roundTo = MB;
                                else if (roundToStr.Equals("G", StringComparison.OrdinalIgnoreCase))
                                    roundTo = GB;
                                else if (roundToStr.Equals("T", StringComparison.OrdinalIgnoreCase))
                                    roundTo = TB;
                                else if (roundToStr.Equals("P", StringComparison.OrdinalIgnoreCase))
                                    roundTo = PB;
                                else
                                    throw new ExecuteErrorException($"[{roundToStr}] is not valid integer");
                            }

                            if (roundTo < 0)
                                throw new ExecuteErrorException($"[{roundTo}] must be positive integer");

                            string srcIntStr = StringEscaper.Preprocess(s, subInfo.SizeVar);
                            if (long.TryParse(srcIntStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long srcInt) == false)
                                throw new ExecuteErrorException($"[{srcIntStr}] is not valid integer");
                            long destInt;
                            if (type == StrFormatType.Ceil)
                            {
                                long remainder = srcInt % roundTo;
                                destInt = srcInt - remainder;
                            }
                            else if (type == StrFormatType.Floor)
                            {
                                long remainder = srcInt % roundTo;
                                destInt = srcInt - remainder + roundTo;
                            }
                            else // if (type == StrFormatType.Round)
                            {
                                long remainder = srcInt % roundTo;
                                if ((roundTo - 1) / 2 < remainder)
                                    destInt = srcInt - remainder + roundTo;
                                else
                                    destInt = srcInt - remainder;
                            }

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.SizeVar, destInt.ToString());
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Date:
                        break;
                    case StrFormatType.FileName:
                    case StrFormatType.DirPath:
                    case StrFormatType.Path:
                    case StrFormatType.Ext:
                        {
                            StrFormatInfo_Path subInfo = info.SubInfo as StrFormatInfo_Path;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.FilePath);

                            string destStr = string.Empty;
                            if (type == StrFormatType.FileName)
                            {
                                destStr = Path.GetFileName(srcStr);
                            }
                            else if (type == StrFormatType.DirPath || type == StrFormatType.Path)
                            {
                                destStr = Path.GetDirectoryName(srcStr);
                            }
                            else if (type == StrFormatType.Ext)
                            {
                                destStr = Path.GetExtension(srcStr);
                            }

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Inc:
                    case StrFormatType.Dec:
                    case StrFormatType.Mult:
                    case StrFormatType.Div:
                        { // Why, why arithmetic is in StrFormat...
                            StrFormatInfo_Arithmetic subInfo = info.SubInfo as StrFormatInfo_Arithmetic;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.DestVarName);
                            if (decimal.TryParse(srcStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal src) == false)
                                throw new ExecuteErrorException($"[{srcStr}] is not valid number");
                            string operandStr = StringEscaper.Preprocess(s, subInfo.Integer);
                            if (decimal.TryParse(operandStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal operand) == false)
                                throw new ExecuteErrorException($"[{operandStr}] is not valid number");

                            decimal dest = src;
                            if (type == StrFormatType.Inc) // +
                                dest += operand;
                            else if (type == StrFormatType.Dec) // -
                                dest -= operand;
                            else if (type == StrFormatType.Mult) // *
                                dest *= operand;
                            else if (type == StrFormatType.Div) // /
                                dest /= operand;

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, dest.ToString());
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Left:
                    case StrFormatType.Right:
                        {
                            StrFormatInfo_LeftRight subInfo = info.SubInfo as StrFormatInfo_LeftRight;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);
                            string cutLenStr = StringEscaper.Preprocess(s, subInfo.Integer);
                            if (int.TryParse(cutLenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cutLen) == false)
                                throw new ExecuteErrorException($"[{cutLenStr}] is not valid integer");
                            if (cutLen < 0)
                                throw new ExecuteErrorException($"[{cutLen}] must be positive integer");

                            string destStr = string.Empty;
                            try
                            {
                                if (type == StrFormatType.Left)
                                {
                                    destStr = srcStr.Substring(0, cutLen);
                                }
                                else if (type == StrFormatType.Right)
                                {
                                    destStr = srcStr.Substring(srcStr.Length - cutLen, cutLen);
                                }

                                List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                                logs.AddRange(LogInfo.AddCommand(varLogs, cmd));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                logs.Add(new LogInfo(LogState.Error, $"[{cutLen}] is not valid index"));
                            }
                        }
                        break;
                    case StrFormatType.SubStr:
                        {
                            StrFormatInfo_SubStr subInfo = info.SubInfo as StrFormatInfo_SubStr;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);
                            string startPosStr = StringEscaper.Preprocess(s, subInfo.StartPos);
                            if (int.TryParse(startPosStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int startPos) == false)
                                throw new ExecuteErrorException($"[{startPosStr}] is not valid integer");
                            if (startPos < 0)
                                throw new ExecuteErrorException($"[{startPos}] must be positive integer");
                            string lenStr = StringEscaper.Preprocess(s, subInfo.Length);
                            if (int.TryParse(lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int len) == false)
                                throw new ExecuteErrorException($"[{lenStr}] is not valid integer");
                            if (len < 0)
                                throw new ExecuteErrorException($"[{len}] must be positive integer");

                            // Error handling
                            if (srcStr.Length <= startPos)
                                logs.Add(new LogInfo(LogState.Error, $"Start position [{startPos}] cannot be bigger than source string's length [{srcStr.Length}]"));
                            if (srcStr.Length - startPos < len)
                                logs.Add(new LogInfo(LogState.Error, $"Length [{len}] cannot be bigger than [{srcStr.Length - startPos}]"));

                            string destStr = srcStr.Substring(startPos, len);

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Len:
                        {
                            StrFormatInfo_Len subInfo = info.SubInfo as StrFormatInfo_Len;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, srcStr.Length.ToString());
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.LTrim:
                    case StrFormatType.RTrim:
                    case StrFormatType.CTrim:
                        {
                            StrFormatInfo_Trim subInfo = info.SubInfo as StrFormatInfo_Trim;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);
                            string toTrim = StringEscaper.Preprocess(s, subInfo.ToTrim);

                            string destStr = string.Empty;
                            try
                            {
                                if (type == StrFormatType.LTrim) // string.Substring
                                {
                                    if (int.TryParse(toTrim, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cutLen) == false)
                                        logs.Add(new LogInfo(LogState.Error, $"[{toTrim}] is not valid integer"));

                                    destStr = srcStr.Substring(cutLen);
                                }
                                else if (type == StrFormatType.RTrim) // string.Substring
                                {
                                    if (int.TryParse(toTrim, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cutLen) == false)
                                        logs.Add(new LogInfo(LogState.Error, $"[{toTrim}] is not valid integer"));

                                    destStr = srcStr.Substring(0, srcStr.Length - cutLen);
                                }
                                else if (type == StrFormatType.CTrim) // string.Trim
                                {
                                    char[] chArr = toTrim.ToCharArray();
                                    destStr = srcStr.Trim(chArr);
                                }

                                List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                                logs.AddRange(varLogs);
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                logs.Add(new LogInfo(LogState.Error, $"[{toTrim}] is not valid index"));
                            }
                        }
                        break;
                    case StrFormatType.NTrim:
                        {
                            StrFormatInfo_NTrim subInfo = info.SubInfo as StrFormatInfo_NTrim;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);

                            Match match = Regex.Match(srcStr, @"([0-9]+)$", RegexOptions.Compiled);
                            string destStr = srcStr.Substring(0, match.Index);

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Pos:
                        {
                            StrFormatInfo_Pos subInfo = info.SubInfo as StrFormatInfo_Pos;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);
                            string subStr = StringEscaper.Preprocess(s, subInfo.SubString);

                            int idx = srcStr.IndexOf(subStr, StringComparison.OrdinalIgnoreCase) + 1;

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, idx.ToString());
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Replace:
                    case StrFormatType.ReplaceX:
                        {
                            StrFormatInfo_Replace subInfo = info.SubInfo as StrFormatInfo_Replace;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);
                            string subStr = StringEscaper.Preprocess(s, subInfo.ToBeReplaced);
                            string newStr = StringEscaper.Preprocess(s, subInfo.ReplaceWith);

                            string destStr;
                            if (type == StrFormatType.Replace)
                            {
                                StringBuilder b = new StringBuilder();
                                int startIdx = 0;
                                int newIdx = srcStr.Substring(startIdx).IndexOf(subStr);
                                while (newIdx != -1)
                                {
                                    b.Append(srcStr.Substring(startIdx, newIdx));
                                    b.Append(newStr);
                                    startIdx = newIdx + subStr.Length;
                                    newIdx = srcStr.Substring(startIdx).IndexOf(subStr);
                                }
                                destStr = b.ToString();
                            }
                            else
                            {
                                destStr = srcStr.Replace(subStr, newStr);
                            }

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.ShortPath:
                    case StrFormatType.LongPath:
                        {
                            StrFormatInfo_ShortLongPath subInfo = info.SubInfo as StrFormatInfo_ShortLongPath;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);

                            string destStr;
                            if (type == StrFormatType.ShortPath)
                            {
                                destStr = FileHelper.GetShortPath(srcStr);
                            }
                            else
                            {
                                destStr = FileHelper.GetLongPath(srcStr);
                            }

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    case StrFormatType.Split:
                        {
                            StrFormatInfo_Split subInfo = info.SubInfo as StrFormatInfo_Split;
                            if (subInfo == null)
                                throw new InternalStrFormatInfoException();

                            string srcStr = StringEscaper.Preprocess(s, subInfo.SrcString);
                            string delimStr = StringEscaper.Preprocess(s, subInfo.Delimeter);
                            string idxStr = StringEscaper.Preprocess(s, subInfo.Index);
                            if (int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) == false)
                                throw new ExecuteErrorException($"[{idxStr}] is not valid integer");

                            char[] delim = delimStr.ToCharArray();

                            string destStr;
                            if (idx == 0)
                            {
                                destStr = srcStr.Split(delim).Length.ToString();
                            }
                            else
                            {
                                string[] slices = srcStr.Split(delim);
                                destStr = slices[idx - 1];
                            }

                            List<LogInfo> varLogs = Variables.SetVariable(s, subInfo.DestVarName, destStr);
                            logs.AddRange(varLogs);
                        }
                        break;
                    // Error
                    default:
                        throw new InvalidCodeCommandException($"Wrong StrFormatType [{type}]");
                }
            }
            catch (InternalStrFormatInfoException)
            {
                logs = new List<LogInfo>() { new LogInfo(LogState.Error, $"Command [StrFormat,{type}] should have [StrFormatInfo_{type}]") };
            }

            return logs;
        }
    }
}