/*
    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RomPatch
{
    /// <summary>
    /// Applies a series of patches to a ROM.
    /// </summary>
    class Patcher
    {
        public const uint BaselineOffset = 0xFF000000;

        private const uint metadataAddress = 0x80001000;
        private const uint requiredVersionPrefix = 0x12340000;
        private const uint calibrationIdPrefix = 0x12340001;
        private const uint patchPrefix = 0x12340002;
        private const uint replace4BytesPrefix = 0x12340003;
        private const uint endOfMetadata = 0x00090009;
        
        /// <summary>
        /// Calibration ID that the given patch file was intended for.
        /// </summary>
        public string InitialCalibrationId { get; private set; }

        /// <summary>
        /// Calibration ID that will be stamped onto the ROM.
        /// </summary>
        public string FinalCalibrationId { get; private set; }

        private readonly SRecordReader reader;
        private readonly Stream romStream;
        private List<Blob> blobs;
        private List<Patch> patches;

        public IList<Patch> Patches { get { return this.patches; } }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Patcher(SRecordReader reader, Stream romStream)
        {
            this.reader = reader;
            this.romStream = romStream;
            this.blobs = new List<Blob>();
            this.patches = new List<Patch>();
        }

        /// <summary>
        /// Create the patch start/end metadata from the patch file.
        /// </summary>
        public bool TryReadPatches()
        {
            if (!this.TryReadBlobs())
            {
                return false;
            }

            Blob metadataBlob;
            if (!this.TryGetBlob(metadataAddress, 10, out metadataBlob))
            {
                Console.WriteLine("This patch file does not contain metadata.");
                return false;
            }

            if (!this.TryReadMetadata(metadataBlob))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Print patch descriptions to the console.
        /// </summary>
        public void PrintPatches()
        {
            foreach (Patch patch in this.patches)
            {
                if (patch.StartAddress > BaselineOffset)
                {
                    continue;
                }

                Console.WriteLine(patch.ToString());
            }
        }

        public bool TryReversePatches()
        {
            List<Blob> newBlobs = new List<Blob>();

            foreach (Patch patch in this.patches)
            {
                Blob baselineBlob;
                if (!this.TryGetBlob(patch.StartAddress + BaselineOffset, patch.Length, out baselineBlob))
                {
                    Console.WriteLine("Patch file does not contain baseline data for patch starting at {0:X8}", patch.StartAddress);
                    return false;
                }

                Blob modifiedBlob;
                if (!this.TryGetBlob(patch.StartAddress, patch.Length, out modifiedBlob))
                {
                    Console.WriteLine("Patch file does not contain modified data for patch starting at {0:X8}", patch.StartAddress);
                    return false;
                }

                baselineBlob = baselineBlob.CloneWithNewStartAddress(baselineBlob.StartAddress - BaselineOffset);
                modifiedBlob = modifiedBlob.CloneWithNewStartAddress(modifiedBlob.StartAddress + BaselineOffset);
                newBlobs.Add(baselineBlob);
                newBlobs.Add(modifiedBlob);
            }

            this.blobs = newBlobs;
            return true;
        }

        /// <summary>
        /// Determine whether the data that the patch was designed to overwrite match what's actually in the ROM.
        /// </summary>
        public bool TryVerifyExpectedData()
        {
            Console.WriteLine("Validating patches...");
            bool allPatchesValid = true;
            foreach (Patch patch in this.patches)
            {
                Console.Write(patch.ToString() + " - ");

                Blob expectedBlob;

                if (!this.TryGetBlob(patch.StartAddress + BaselineOffset, patch.Length, out expectedBlob))
                {
                    Console.WriteLine("Failed.");
                    Console.WriteLine("Patch file does not contain baseline data for patch starting at {0:X8}", patch.StartAddress);
                    allPatchesValid = false;
                    continue;
                }

                expectedBlob = expectedBlob.CloneWithNewStartAddress(expectedBlob.StartAddress - BaselineOffset);
                if (!this.ValidateBytes(patch, expectedBlob))
                {
                    // Pass/fail message is printed by ValidateBytes().
                    allPatchesValid = false;
                }
            }

            if (!allPatchesValid)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Try to apply the patches to the ROM.
        /// </summary>
        public bool TryApplyPatches()
        {
            foreach (Patch patch in this.patches)
            {
                if (!this.TryApplyPatch(patch))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Extract actual ROM data, for appending to a patch file.
        /// </summary>
        public bool TryPrintBaselines()
        {
            bool result = true;
            foreach (Patch patch in this.patches)
            {
                if (!this.TryPrintBaseline(patch))
                {
                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Try to read blobs from the patch file.
        /// </summary>
        private bool TryReadBlobs()
        {
            BlobList list = new BlobList();

            this.reader.Open();
            SRecord record;
            while (this.reader.TryReadNextRecord(out record))
            {
                if (!record.IsValid)
                {
                    Console.WriteLine("The patch file contains garbage - was it corrupted somehow?");
                    Console.WriteLine("Line {0}: {1}", record.LineNumber, record.RawData);
                    return false;
                }

                list.ProcessRecord(record);
            }

            this.blobs = list.Blobs;

            return true;
        }

        /// <summary>
        /// Try to read the patch file metadata (start and end addresses of each patch, etc).
        /// </summary>
        private bool TryReadMetadata(Blob blob)
        {
            int offset = 0;
            
            if (!TryConfirmPatchVersion(blob, ref offset))
            {
                return false;
            }

            if (!TryReadCalibrationChange(blob, ref offset))
            {
                return false;
            }

            if (!this.TryReadPatches(blob, ref offset))
            {
                return false;
            }

            if (this.patches.Count == 0)
            {
                Console.WriteLine("This patch file contains no patches.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Try to read the 'required version' metadata.
        /// </summary>
        private bool TryConfirmPatchVersion(Blob blob, ref int offset)
        {
            uint tempUInt32 = 0;

            if (!blob.TryGetUInt32(ref tempUInt32, ref offset))
            {
                Console.WriteLine("This patch file's metadata is way too short (no version metadata).");
                return false;
            }

            if (tempUInt32 != requiredVersionPrefix)
            {
                Console.WriteLine("This patch file's metadata starts with {0}, it should start with {1}", tempUInt32, requiredVersionPrefix);
                return false;
            }

            if (!blob.TryGetUInt32(ref tempUInt32, ref offset))
            {
                Console.WriteLine("This patch file's metadata is way too short (no version).");
                return false;
            }

            if (tempUInt32 != Program.Version)
            {
                Console.WriteLine("This is RomPatch.exe version {0}.", Program.Version);
                Console.WriteLine("This patch file requires version {0}.", tempUInt32);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Try to read the initial and final calibration IDs
        /// </summary>
        private bool TryReadCalibrationChange(Blob blob, ref int offset)
        {
            uint tempUInt32 = 0;

            if (!blob.TryGetUInt32(ref tempUInt32, ref offset))
            {
                Console.WriteLine("This patch file's metadata is way too short (no calibration metadata).");
                return false;
            }

            if (tempUInt32 != calibrationIdPrefix)
            {
                Console.WriteLine("Expected calibration id prefix {0:X8}, found {1:X8}", calibrationIdPrefix, tempUInt32);
                return false;
            }

            if (!blob.TryGetUInt32(ref tempUInt32, ref offset))
            {
                Console.WriteLine("This patch file's metadata is way too short (no calibration address).");
                return false;
            }

            uint calibrationAddress = tempUInt32;

            if (!blob.TryGetUInt32(ref tempUInt32, ref offset))
            {
                Console.WriteLine("This patch file's metadata is way too short (no calibration length).");
                return false;
            }

            uint calibrationLength = tempUInt32;

            string initialCalibrationId;
            if (!this.TryReadCalibrationId(blob, ref offset, out initialCalibrationId))
            {
                return false;
            }

            this.InitialCalibrationId = initialCalibrationId;

            string finalCalibrationId;
            if (!this.TryReadCalibrationId(blob, ref offset, out finalCalibrationId))
            {
                return false;
            }

            this.FinalCalibrationId = finalCalibrationId;

            // Synthesize calibration-change patch and blobs.
            // Seemed like a good idea at the time, but it's not.
            // The .text section can overwrite these pseudo-sections, corrupting the patch file. Should have seen that coming.
            Patch patch = new Patch(
                calibrationAddress, 
                calibrationAddress + (calibrationLength - 1));
            this.patches.Add(patch);

            Blob initialBlob = new Blob(
                calibrationAddress + Patcher.BaselineOffset, 
                Encoding.ASCII.GetBytes(initialCalibrationId));
            this.blobs.Add(initialBlob);

            Blob finalBlob = new Blob(
                calibrationAddress, 
                Encoding.ASCII.GetBytes(finalCalibrationId));
            this.blobs.Add(finalBlob);

            return true;
        }

        /// <summary>
        /// Try to read the calibration ID from the patch metadata.
        /// </summary>
        private bool TryReadCalibrationId(Blob blob, ref int offset, out string calibrationId)
        {
            calibrationId = string.Empty;
            List<byte> calibrationIdBytes = new List<byte>();

            byte tempByte = 0;
            for (int index = 0; index < 16; index++)
            {
                if (!blob.TryGetByte(ref tempByte, ref offset))
                {
                    Console.WriteLine("This patch file's metadata ran out before the complete calibration ID could be found.");
                    return false;
                }

                if (calibrationId == string.Empty)
                {
                    if (tempByte != 0)
                    {
                        calibrationIdBytes.Add(tempByte);
                    }
                    else
                    {
                        calibrationId = System.Text.Encoding.ASCII.GetString(calibrationIdBytes.ToArray());
                    }
                }
                else
                {
                    if (tempByte != 0)
                    {
                        Console.WriteLine("This patch file's metadata contains garbage after the calibration ID.");
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Try to read the Patch metadata from the file.
        /// </summary>
        private bool TryReadPatches(Blob metadata, ref int offset)
        {
            UInt32 cookie = 0;
            while ((metadata.Content.Count > offset + 8) &&
                metadata.TryGetUInt32(ref cookie, ref offset))
            {
                Patch patch = null;
                    
                if (cookie == patchPrefix)
                {
                    if (!this.TryReadPatch(metadata, out patch, ref offset))
                    {
                        Console.WriteLine("Invalid patch found." + patch.ToString());
                        return false;
                    }
                }
                else if (cookie == replace4BytesPrefix)
                {
                    if (!this.TrySynthesize4BytePatch(metadata, out patch, ref offset))
                    {
                        Console.WriteLine("Invalid 4-byte patch found." + patch.ToString());
                        return false;
                    }
                }
                else if (cookie == endOfMetadata)
                {
                    break;
                }

                if (patch == null)
                {
                    throw new Exception(
                        string.Format(
                            "The metadata contains unexpected data following the patch descriptions.{0}" +
                            "Found {1:X8} at {2:X8} while looking for the next patch.",
                            Environment.NewLine,
                            cookie,
                            offset));
                }

                this.patches.Add(patch);
            }

            if (this.patches.Count == 0)
            {
                Console.WriteLine("This patch file's metadata contains no patches.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Read a single Patch from the metadata blob.
        /// </summary>
        /// <remarks>
        /// Consider returning false, printing error message.  But, need to 
        /// be certain to abort the whole process at that point...
        /// </remarks>
        private bool TryReadPatch(Blob metadata, out Patch patch, ref int offset)
        {
            uint start = 0;
            uint end = 0;
                        
            if (!metadata.TryGetUInt32(ref start, ref offset))
            {
                throw new InvalidDataException("This patch's metadata contains an incomplete patch record (no start address).");
            }
                        
            if (!metadata.TryGetUInt32(ref end, ref offset))
            {
                throw new InvalidDataException("This patch's metadata contains an incomplete patch record (no end address).");
            }

            patch = new Patch(start, end);
            return true;
        }

        /// <summary>
        /// Construct a 4-byte patch from the metadata blob.
        /// </summary>
        private bool TrySynthesize4BytePatch(Blob metadata, out Patch patch, ref int offset)
        {
            uint address = 0;
            uint oldValue = 0;
            uint newValue = 0;

            if (!metadata.TryGetUInt32(ref address, ref offset))
            {
                throw new InvalidDataException("This patch's metadata contains an incomplete 4-byte patch record (no address).");
            }

            if (!metadata.TryGetUInt32(ref oldValue, ref offset))
            {
                throw new InvalidDataException("This patch's metadata contains an incomplete 4-byte patch record (no baseline value).");
            }

            if (!metadata.TryGetUInt32(ref newValue, ref offset))
            {
                throw new InvalidDataException("This patch's metadata contains an incomplete 4-byte patch record (no patch value).");
            }

            patch = new Patch(address, address + 3);
            this.blobs.Add(new Blob(address, BitConverter.GetBytes(newValue).Reverse()));
            this.blobs.Add(new Blob(address + BaselineOffset, BitConverter.GetBytes(oldValue).Reverse()));
            return true;
        }

        /// <summary>
        /// Print the current contents of the ROM in the address range for the given patch.
        /// </summary>
        private bool TryPrintBaseline(Patch patch)
        {
            uint patchLength = patch.Length;
            byte[] buffer = new byte[patchLength];
            if (!this.TryReadBuffer(this.romStream, patch.StartAddress, buffer))
            {
                return false;
            }

            using (Stream consoleOutputStream = Console.OpenStandardOutput())
            {
                TextWriter textWriter = new StreamWriter(consoleOutputStream);
                SRecordWriter writer = new SRecordWriter(textWriter);

                // The "baselineOffset" delta is how we distinguish baseline data from patch data.
                writer.Write(patch.StartAddress + BaselineOffset, buffer);
            }

            return true;
        }

        /// <summary>
        /// Try to read an arbitrary byte range from the ROM.
        /// </summary>
        private bool TryReadBuffer(Stream stream, uint startAddress, byte[] buffer)
        {
            stream.Seek(startAddress, SeekOrigin.Begin);
            long totalBytesRead = 0;
            long totalBytesToRead = buffer.Length;

            while (totalBytesRead < totalBytesToRead)
            {
                long bytesToRead = totalBytesToRead - totalBytesRead;
                int bytesRead = this.romStream.Read(
                    buffer,
                    (int) totalBytesRead,
                    (int) bytesToRead);

                if (bytesRead == 0)
                {
                    Console.WriteLine(
                        "Unable to read {0} bytes starting at position {1:X8}",
                        bytesToRead,
                        startAddress + totalBytesRead);
                    return false;
                }

                totalBytesRead += bytesRead;
            }

            return true;
        }

        /// <summary>
        /// Determine whether the bytes from a Patch's expected data match the contents of the ROM.
        /// </summary>
        private bool ValidateBytes(Patch patch, Blob expectedData)
        {
            uint patchLength = patch.Length;
            byte[] buffer = new byte[patchLength];
            if (!this.TryReadBuffer(this.romStream, patch.StartAddress, buffer))
            {
                return false;
            }

            //            DumpBuffer("Actual  ", buffer, buffer.Length);
            //            DumpBuffer("Expected", expectedData.Content, buffer.Length);

            int mismatches = 0;
            if (patch.StartAddress < expectedData.StartAddress)
            {
                Console.WriteLine("Patch start address is lower than expected data start address.");
                return false;
            }

            int offset = (int)(patch.StartAddress - expectedData.StartAddress);
            for (int index = 0; index < patchLength; index++)
            {
                byte actual = buffer[index];

                if (index >= expectedData.Content.Count)
                {
                    Console.WriteLine("Expected data is smaller than patch size.");
                    return false;
                }

                byte expected = expectedData.Content[index + offset];

                if (actual != expected)
                {
                    mismatches++;
                }
            }

            if (mismatches == 0)
            {
                Console.WriteLine("Valid.");
                return true;
            }

            Console.WriteLine("Invalid.");
            Console.WriteLine("{0} bytes (of {1}) do not meet expectations.", mismatches, patchLength);
            return false;
        }

        /// <summary>
        /// Try to find a blob that starts at the given address.
        /// </summary>
        private bool TryGetBlob(uint startAddress, uint length, out Blob match)
        {
            foreach (Blob blob in this.blobs)
            {
                bool startsInsideBlob = blob.StartAddress <= startAddress;
                if (!startsInsideBlob)
                {
                    continue;
                }

                uint blobEndAddress = (uint)(blob.StartAddress + blob.Content.Count);
                bool endsInsideBlob = startAddress + length <= blobEndAddress;
                if (!endsInsideBlob)
                {
                    continue;
                }

                match = blob;
                return true;
            }

            match = null;
            return false;
        }

        /// <summary>
        /// Try to find a blob that contains the data for the given Patch.
        /// </summary>
        private bool TryGetBlob(Patch patch, out Blob match)
        {
            return TryGetBlob(patch.StartAddress, patch.Length, out match);
        }

        /// <summary>
        /// Given a patch, look up the content Blob and write it into the ROM.
        /// </summary>
        private bool TryApplyPatch(Patch patch)
        {
            this.romStream.Seek(patch.StartAddress, SeekOrigin.Begin);

            Blob blob;
            
            if (!this.TryGetBlob(patch, out blob))
            {
                Console.WriteLine("No blob found for patch starting at {0:X8}", patch.StartAddress);
                return false;
            }

            int startIndex = (int) patch.StartAddress - (int) blob.StartAddress;
            if (startIndex >= blob.Content.Count)
            {
                Console.WriteLine("Blob for patch starting at {0:X8} does not contain the start of the patch itself.", patch.StartAddress);
                return false;
            }

            if (startIndex + patch.Length > blob.Content.Count)
            {
                Console.WriteLine("Blob for patch starting at {0:X8} does not contain the entire patch.", patch.StartAddress);
                Console.WriteLine("Patch start {0:X8}, end {1:X8}, length {2:X8}", patch.StartAddress, patch.EndAddress, patch.Length);
                Console.WriteLine("Blob  start {0:X8}, end {1:X8}, length {2:X8}", blob.StartAddress, blob.NextByteAddress - 1, blob.Content.Count);
                return false;
            }

            this.romStream.Write(blob.Content.ToArray(), startIndex, (int) patch.Length);
            return true;
        }
    }
}
