using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Category lists are parsed in one call, so one off-type field used to fail the whole list
    /// (ADR-010). The live categories endpoint turned that into a server error and the VOD and
    /// series endpoints into an empty list. The converters sit on the model, so this holds for
    /// every caller whatever options it passes.
    /// </summary>
    public class CategoryModelTests
    {
        // The options the live categories path uses: no converters registered.
        private static readonly JsonSerializerOptions PlainOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };

        [Theory]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("[]")]
        [InlineData("\"none\"")]
        public void OffTypeParentId_DoesNotFailTheList(string parentId)
        {
            var json = "[{\"category_id\":\"5\",\"category_name\":\"News\",\"parent_id\":" + parentId + "}," +
                       "{\"category_id\":\"6\",\"category_name\":\"Sport\",\"parent_id\":0}]";

            var categories = JsonSerializer.Deserialize<List<Category>>(json, PlainOptions);

            Assert.Equal(2, categories.Count);
            Assert.Equal(5, categories[0].CategoryId);
            Assert.Null(categories[0].ParentId);
            Assert.Equal(0, categories[1].ParentId);
        }

        [Fact]
        public void NumericCategoryName_IsReadAsText()
        {
            var json = "[{\"category_id\":\"7\",\"category_name\":2024,\"parent_id\":0}]";

            var categories = JsonSerializer.Deserialize<List<Category>>(json, PlainOptions);

            Assert.Equal("2024", categories[0].CategoryName);
        }

        [Fact]
        public void MissingCategoryId_StillFails()
        {
            // The primary ID stays strict: a category without a usable ID should fail loudly,
            // not turn into category 0.
            var json = "[{\"category_id\":\"\",\"category_name\":\"News\"}]";

            Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<List<Category>>(json, PlainOptions));
        }
    }
}
