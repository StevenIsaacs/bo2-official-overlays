/*
    benofficial2's Official Overlays
    Copyright (C) 2025-2026 benofficial2

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using benofficial2.Plugin;

namespace benofficial2.Tests
{
    [TestClass]
    public sealed class MyRaceCraftCaptureTests
    {
        [TestMethod]
        public void GetFlagString_Green()
        {
            Assert.AreEqual("green", MyRaceCraftCaptureModule.GetFlagString(0x0));
            Assert.AreEqual("green", MyRaceCraftCaptureModule.GetFlagString(0x4));   // irsdk_green
            Assert.AreEqual("green", MyRaceCraftCaptureModule.GetFlagString(0x400)); // irsdk_green_held
        }

        [TestMethod]
        public void GetFlagString_Yellow()
        {
            Assert.AreEqual("yellow", MyRaceCraftCaptureModule.GetFlagString(0x8));    // irsdk_yellow
            Assert.AreEqual("yellow", MyRaceCraftCaptureModule.GetFlagString(0x100));  // irsdk_yellow_waving
            Assert.AreEqual("yellow", MyRaceCraftCaptureModule.GetFlagString(0x4000)); // irsdk_caution
            Assert.AreEqual("yellow", MyRaceCraftCaptureModule.GetFlagString(0x8000)); // irsdk_caution_waving
        }

        [TestMethod]
        public void GetFlagString_Checkered()
        {
            Assert.AreEqual("checkered", MyRaceCraftCaptureModule.GetFlagString(0x1)); // irsdk_checkered
        }

        [TestMethod]
        public void GetFlagString_Red()
        {
            Assert.AreEqual("red", MyRaceCraftCaptureModule.GetFlagString(0x10)); // irsdk_red
        }

        [TestMethod]
        public void GetFlagString_Blue()
        {
            Assert.AreEqual("blue", MyRaceCraftCaptureModule.GetFlagString(0x20)); // irsdk_blue
        }

        [TestMethod]
        public void GetFlagString_Precedence()
        {
            // Checkered takes precedence over yellow.
            Assert.AreEqual("checkered", MyRaceCraftCaptureModule.GetFlagString(0x1 | 0x8));
        }
    }
}
