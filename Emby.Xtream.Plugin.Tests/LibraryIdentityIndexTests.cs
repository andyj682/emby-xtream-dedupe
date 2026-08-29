using System.Collections.Generic;
using System.IO;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for BuildLibraryIdentityIndex, the on-disk record the review gate uses to recognise
    /// titles the user has already chosen to keep.
    ///
    /// Two properties matter here and they pull in opposite directions. The walk must stay
    /// RECURSIVE, because in Multiple/Custom folder mode a show sits one level deeper than in
    /// single-folder mode and a shallow walk would index category folders instead of shows —
    /// the gate would then withhold shows that are on disk. But recursing also picks up season
    /// subfolders, which made the logged count overstate ("934 shows already on disk" against
    /// 881 real ones). So the index keeps everything and only the returned COUNT excludes
    /// seasons. These tests pin both halves, so a later attempt to "tidy" the walk fails loudly
    /// instead of quietly breaking the gate.
    ///
    /// Fixture names are deliberately synthetic rather than titles from a real library.
    /// </summary>
    public class LibraryIdentityIndexTests
    {
        private static int Index(string libraryPath, string rootFolder,
            out HashSet<int> tmdbIds, out HashSet<string> folderNames)
        {
            tmdbIds = new HashSet<int>();
            folderNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            return StrmSyncService.BuildLibraryIdentityIndex(libraryPath, rootFolder, tmdbIds, folderNames);
        }

        [Fact]
        public void CountsShowsNotSeasonSubfolders()
        {
            using (var temp = new TempDirectory())
            {
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "First Show (2001) [tmdbid=101]", "Season 01"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "First Show (2001) [tmdbid=101]", "Season 02"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Second Show (2002) [tmdbid=202]", "Season 01"));

                HashSet<int> tmdbIds;
                HashSet<string> folderNames;
                var count = Index(temp.Path, "Shows", out tmdbIds, out folderNames);

                // Two shows on disk, not five directories.
                Assert.Equal(2, count);

                // ...but the index itself is untouched: the season names are still in there,
                // because nothing may change about what the gate can match on.
                Assert.Contains("First Show (2001)", folderNames);
                Assert.Contains("Second Show (2002)", folderNames);
                Assert.Contains("Season 01", folderNames);
                Assert.Contains("Season 02", folderNames);
                Assert.Equal(4, folderNames.Count);

                Assert.Contains(101, tmdbIds);
                Assert.Contains(202, tmdbIds);
            }
        }

        /// <summary>
        /// Multiple/Custom folder mode: shows live at Shows/&lt;Category&gt;/&lt;Show&gt;. This is
        /// the case a top-level-only walk would break, and the reason the recursion has to stay.
        /// </summary>
        [Fact]
        public void FindsShowsNestedUnderCategoryFolders()
        {
            using (var temp = new TempDirectory())
            {
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Category A", "Nested Show (2003) [tmdbid=303]", "Season 04"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Category B", "Other Nested Show (2004) [tmdbid=404]"));

                HashSet<int> tmdbIds;
                HashSet<string> folderNames;
                var count = Index(temp.Path, "Shows", out tmdbIds, out folderNames);

                // The shows must be matchable by name, or the gate withholds titles already on disk.
                Assert.Contains("Nested Show (2003)", folderNames);
                Assert.Contains("Other Nested Show (2004)", folderNames);
                Assert.Contains(303, tmdbIds);
                Assert.Contains(404, tmdbIds);

                // The two category folders are counted as titles: they are indistinguishable from
                // shows without knowing the folder mode, and over-counting a log line is the
                // harmless direction. Seasons are still excluded.
                Assert.Equal(4, count);
            }
        }

        [Fact]
        public void MoviesHaveNoSubfoldersSoCountMatchesTheIndex()
        {
            using (var temp = new TempDirectory())
            {
                Directory.CreateDirectory(Path.Combine(temp.Path, "Movies", "First Film (2005) [tmdbid=505]"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Movies", "Second Film (2006) [tmdbid=606]"));

                HashSet<int> tmdbIds;
                HashSet<string> folderNames;
                var count = Index(temp.Path, "Movies", out tmdbIds, out folderNames);

                Assert.Equal(2, count);
                Assert.Equal(folderNames.Count, count);
            }
        }

        [Fact]
        public void OnlyExcludesTheExactSeasonFormatThePluginWrites()
        {
            using (var temp = new TempDirectory())
            {
                // The plugin writes "Season {0:D2}", and the exclusion is a whole-name match. A
                // title that merely starts with the word, or has anything after the digits, is a
                // show and must still count — the trailing anchor is what makes that true.
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Season Of Testing (2007)"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Seasons Of Testing (2008)"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Season 3 Of Testing (2009)"));
                Directory.CreateDirectory(Path.Combine(temp.Path, "Shows", "Fourth Show (2010)", "Season 01"));

                HashSet<int> tmdbIds;
                HashSet<string> folderNames;
                var count = Index(temp.Path, "Shows", out tmdbIds, out folderNames);

                Assert.Equal(4, count);
                Assert.Contains("Season Of Testing (2007)", folderNames);
                Assert.Contains("Seasons Of Testing (2008)", folderNames);
                Assert.Contains("Season 3 Of Testing (2009)", folderNames);
            }
        }

        [Fact]
        public void MissingRootReturnsZeroAndLeavesTheSetsEmpty()
        {
            using (var temp = new TempDirectory())
            {
                HashSet<int> tmdbIds;
                HashSet<string> folderNames;
                var count = Index(temp.Path, "Shows", out tmdbIds, out folderNames);

                Assert.Equal(0, count);
                Assert.Empty(folderNames);
                Assert.Empty(tmdbIds);
            }
        }
    }
}
