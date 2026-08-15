using EmbyCredits.Services.Utilities;
using Xunit;

namespace EmbyCredits.Tests
{
    public class EpisodeEligibilityTests
    {
        [Theory]
        [InlineData("/media/show/episode.strm")]
        [InlineData("/media/show/episode.STRM")]
        [InlineData("C:\\media\\show\\episode.StrM")]
        public void IsStrmPath_ReturnsTrueForStrmExtensions(string path)
        {
            Assert.True(EpisodeEligibility.IsStrmPath(path));
        }

        [Theory]
        [InlineData("/media/show/episode.mkv")]
        [InlineData("/media/show/episode.mp4")]
        [InlineData("")]
        [InlineData(null)]
        public void IsStrmPath_ReturnsFalseForOtherPaths(string? path)
        {
            Assert.False(EpisodeEligibility.IsStrmPath(path));
        }
    }
}
