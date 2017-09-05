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
using System.Linq;
using System.Text;

namespace RomPatch
{
    /// <summary>
    /// Describes the start and end addresses of an ECU patch (but not the content).
    /// </summary>
    /// <remarks>
    /// Corresponds to the metadata in a patch file.  See sample HEW project for details.
    /// </remarks>
    class Patch
    {
        /// <summary>
        /// Start address of the patch (the first byte to overwrite).
        /// </summary>
        public uint StartAddress { get; private set; }

        /// <summary>
        /// End address of the patch (the last byte to overwrite).
        /// </summary>
        public uint EndAddress { get; private set; }

        /// <summary>
        /// Length of the patch.
        /// </summary>
        public uint Length { get { return (this.EndAddress+1) - this.StartAddress; } }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Patch(uint start, uint end)
        {
            this.StartAddress = start;
            this.EndAddress = end;
        }

        /// <summary>
        /// Describe the patch range in human terms.
        /// </summary>
        public override string ToString()
        {
            return string.Format("Patch start: {0:X8}, end: {1:X8}, length: {2:X8}", this.StartAddress, this.EndAddress, this.Length);
        }
    }
}
