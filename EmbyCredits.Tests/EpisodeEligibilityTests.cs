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

        [Theory]
        [InlineData("/media/show/episode.strm", true, true)]
        [InlineData("/media/show/episode.STRM", true, true)]
        [InlineData("/media/show/episode.strm", false, false)]
        [InlineData("/media/show/episode.mkv", true, false)]
        public void ShouldSkipMediaProcessingHonorsConfiguration(string path, bool enabled, bool expected)
        {
            Assert.Equal(expected, EpisodeEligibility.ShouldSkipMediaProcessing(path, enabled));
        }
    }
}
