using System.Buffers.Binary;
using System.Text;
using Avalonia.Media.Imaging;
using Xunit;

namespace Avalonia.Base.UnitTests.Media.Imaging
{
    public class CmykProfilesTests
    {
        [Fact]
        public void SwopCoated_Should_Be_A_Cmyk_Printer_Profile_To_Lab()
        {
            var profile = CmykProfiles.SwopCoated;

            Assert.Equal(profile.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(profile));
            Assert.Equal("prtrCMYKLab ", Encoding.ASCII.GetString(profile, 12, 12));
            Assert.Equal("acsp", Encoding.ASCII.GetString(profile, 36, 4));
        }

        [Fact]
        public void SwopCoated_Should_Name_Its_Characterization_Data()
        {
            var text = Encoding.ASCII.GetString(CmykProfiles.SwopCoated);

            Assert.Contains("ANSI/CGATS/SWOP TR 003-2007", text);
        }

        [Fact]
        public void SwopCoated_Should_Return_A_Copy()
        {
            var first = CmykProfiles.SwopCoated;

            first[0] = 0xFF;

            Assert.NotEqual(first[0], CmykProfiles.SwopCoated[0]);
        }
    }
}
