﻿using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MoreLinq;
using ServiceStack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Api.Movies
{
    /// <summary>
    /// Class GetSimilarMovies
    /// </summary>
    [Route("/Movies/{Id}/Similar", "GET", Summary = "Finds movies and trailers similar to a given movie.")]
    public class GetSimilarMovies : BaseGetSimilarItemsFromItem
    {
    }

    [Route("/Movies/Recommendations", "GET", Summary = "Gets movie recommendations")]
    public class GetMovieRecommendations : IReturn<RecommendationDto[]>, IHasItemFields
    {
        [ApiMember(Name = "CategoryLimit", Description = "The max number of categories to return", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int CategoryLimit { get; set; }

        [ApiMember(Name = "ItemLimit", Description = "The max number of items to return per category", IsRequired = false, DataType = "int", ParameterType = "query", Verb = "GET")]
        public int ItemLimit { get; set; }

        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        /// <value>The user id.</value>
        [ApiMember(Name = "UserId", Description = "Optional. Filter by user id, and attach user data", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public Guid? UserId { get; set; }

        /// <summary>
        /// Specify this to localize the search to a specific item or folder. Omit to use the root.
        /// </summary>
        /// <value>The parent id.</value>
        [ApiMember(Name = "ParentId", Description = "Specify this to localize the search to a specific item or folder. Omit to use the root", IsRequired = false, DataType = "string", ParameterType = "query", Verb = "GET")]
        public string ParentId { get; set; }

        public GetMovieRecommendations()
        {
            CategoryLimit = 5;
            ItemLimit = 8;
        }

        public string Fields { get; set; }
    }

    /// <summary>
    /// Class MoviesService
    /// </summary>
    [Authenticated]
    public class MoviesService : BaseApiService
    {
        /// <summary>
        /// The _user manager
        /// </summary>
        private readonly IUserManager _userManager;

        /// <summary>
        /// The _user data repository
        /// </summary>
        private readonly IUserDataManager _userDataRepository;
        /// <summary>
        /// The _library manager
        /// </summary>
        private readonly ILibraryManager _libraryManager;

        private readonly IItemRepository _itemRepo;
        private readonly IDtoService _dtoService;

        private readonly IChannelManager _channelManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="MoviesService"/> class.
        /// </summary>
        /// <param name="userManager">The user manager.</param>
        /// <param name="userDataRepository">The user data repository.</param>
        /// <param name="libraryManager">The library manager.</param>
        public MoviesService(IUserManager userManager, IUserDataManager userDataRepository, ILibraryManager libraryManager, IItemRepository itemRepo, IDtoService dtoService, IChannelManager channelManager)
        {
            _userManager = userManager;
            _userDataRepository = userDataRepository;
            _libraryManager = libraryManager;
            _itemRepo = itemRepo;
            _dtoService = dtoService;
            _channelManager = channelManager;
        }

        /// <summary>
        /// Gets the specified request.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns>System.Object.</returns>
        public async Task<object> Get(GetSimilarMovies request)
        {
            var result = await GetSimilarItemsResult(
                // Strip out secondary versions
                request, item => (item is Movie) && !((Video)item).PrimaryVersionId.HasValue,

                SimilarItemsHelper.GetSimiliarityScore).ConfigureAwait(false);

            return ToOptimizedSerializedResultUsingCache(result);
        }

        public async Task<object> Get(GetMovieRecommendations request)
        {
            var user = _userManager.GetUserById(request.UserId.Value);

            var movies = GetAllLibraryItems(request.UserId, _userManager, _libraryManager, request.ParentId)
                .Where(i => i is Movie);

            movies = _libraryManager.ReplaceVideosWithPrimaryVersions(movies);

            var listEligibleForCategories = new List<BaseItem>();
            var listEligibleForSuggestion = new List<BaseItem> ();

            var list = movies.ToList();

            listEligibleForCategories.AddRange(list);
            listEligibleForSuggestion.AddRange(list);

            if (user.Configuration.IncludeTrailersInSuggestions)
            {
                var trailerResult = await _channelManager.GetAllMediaInternal(new AllChannelMediaQuery
                {
                    ContentTypes = new[] { ChannelMediaContentType.MovieExtra },
                    ExtraTypes = new[] { ExtraType.Trailer },
                    UserId = user.Id.ToString("N")

                }, CancellationToken.None).ConfigureAwait(false);

                listEligibleForSuggestion.AddRange(trailerResult.Items);
            }

            listEligibleForCategories = listEligibleForCategories
                .DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(i => i.GetProviderId(MetadataProviders.Imdb) ?? Guid.NewGuid().ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            listEligibleForSuggestion = listEligibleForSuggestion
                .DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(i => i.GetProviderId(MetadataProviders.Imdb) ?? Guid.NewGuid().ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = GetRecommendationCategories(user, listEligibleForCategories, listEligibleForSuggestion, request.CategoryLimit, request.ItemLimit, request.GetItemFields().ToList());

            return ToOptimizedResult(result);
        }

        private async Task<ItemsResult> GetSimilarItemsResult(BaseGetSimilarItemsFromItem request, Func<BaseItem, bool> includeInSearch, Func<BaseItem, BaseItem, int> getSimilarityScore)
        {
            var user = request.UserId.HasValue ? _userManager.GetUserById(request.UserId.Value) : null;

            var item = string.IsNullOrEmpty(request.Id) ?
                (request.UserId.HasValue ? user.RootFolder :
                _libraryManager.RootFolder) : _libraryManager.GetItemById(request.Id);

            var fields = request.GetItemFields().ToList();

            var inputItems = user == null
                                 ? _libraryManager.RootFolder.GetRecursiveChildren().Where(i => i.Id != item.Id)
                                 : user.RootFolder.GetRecursiveChildren(user).Where(i => i.Id != item.Id);

            inputItems = inputItems.Where(includeInSearch);

            var list = inputItems.ToList();

            if (item is Movie && user != null && user.Configuration.IncludeTrailersInSuggestions)
            {
                var trailerResult = await _channelManager.GetAllMediaInternal(new AllChannelMediaQuery
                {
                    ContentTypes = new[] { ChannelMediaContentType.MovieExtra },
                    ExtraTypes = new[] { ExtraType.Trailer },
                    UserId = user.Id.ToString("N")

                }, CancellationToken.None).ConfigureAwait(false);

                var newTrailers = trailerResult.Items;

                list.AddRange(newTrailers);

                list = list
                    .DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .DistinctBy(i => i.GetProviderId(MetadataProviders.Imdb) ?? Guid.NewGuid().ToString(), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (item is Video)
            {
                var imdbId = item.GetProviderId(MetadataProviders.Imdb);

                // Use imdb id to try to filter duplicates of the same item
                if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    list = list
                        .Where(i => !string.Equals(imdbId, i.GetProviderId(MetadataProviders.Imdb), StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
            }

            var items = SimilarItemsHelper.GetSimilaritems(item, list, getSimilarityScore).ToList();

            IEnumerable<BaseItem> returnItems = items;

            if (request.Limit.HasValue)
            {
                returnItems = returnItems.Take(request.Limit.Value);
            }
          
            var result = new ItemsResult
            {
                Items = returnItems.Select(i => _dtoService.GetBaseItemDto(i, fields, user)).ToArray(),

                TotalRecordCount = items.Count
            };

            return result;
        }

        private IEnumerable<RecommendationDto> GetRecommendationCategories(User user, List<BaseItem> allMoviesForCategories, List<BaseItem> allMovies, int categoryLimit, int itemLimit, List<ItemFields> fields)
        {
            var categories = new List<RecommendationDto>();

            var recentlyPlayedMovies = allMoviesForCategories
                .Select(i =>
                {
                    var userdata = _userDataRepository.GetUserData(user.Id, i.GetUserDataKey());
                    return new Tuple<BaseItem, bool, DateTime>(i, userdata.Played, userdata.LastPlayedDate ?? DateTime.MinValue);
                })
                .Where(i => i.Item2)
                .OrderByDescending(i => i.Item3)
                .Select(i => i.Item1)
                .ToList();

            var excludeFromLiked = recentlyPlayedMovies.Take(10);
            var likedMovies = allMovies
                .Select(i =>
                {
                    var score = 0;
                    var userData = _userDataRepository.GetUserData(user.Id, i.GetUserDataKey());

                    if (userData.IsFavorite)
                    {
                        score = 2;
                    }
                    else
                    {
                        score = userData.Likes.HasValue ? userData.Likes.Value ? 1 : -1 : 0;
                    }

                    return new Tuple<BaseItem, int>(i, score);
                })
                .OrderByDescending(i => i.Item2)
                .ThenBy(i => Guid.NewGuid())
                .Where(i => i.Item2 > 0)
                .Select(i => i.Item1)
                .Where(i => !excludeFromLiked.Contains(i));

            var mostRecentMovies = recentlyPlayedMovies.Take(6).ToList();
            // Get recently played directors
            var recentDirectors = GetDirectors(mostRecentMovies)
                .OrderBy(i => Guid.NewGuid())
                .ToList();

            // Get recently played actors
            var recentActors = GetActors(mostRecentMovies)
                .OrderBy(i => Guid.NewGuid())
                .ToList();

            var similarToRecentlyPlayed = GetSimilarTo(user, allMovies, recentlyPlayedMovies.Take(7).OrderBy(i => Guid.NewGuid()), itemLimit, fields, RecommendationType.SimilarToRecentlyPlayed).GetEnumerator();
            var similarToLiked = GetSimilarTo(user, allMovies, likedMovies, itemLimit, fields, RecommendationType.SimilarToLikedItem).GetEnumerator();

            var hasDirectorFromRecentlyPlayed = GetWithDirector(user, allMovies, recentDirectors, itemLimit, fields, RecommendationType.HasDirectorFromRecentlyPlayed).GetEnumerator();
            var hasActorFromRecentlyPlayed = GetWithActor(user, allMovies, recentActors, itemLimit, fields, RecommendationType.HasActorFromRecentlyPlayed).GetEnumerator();

            var categoryTypes = new List<IEnumerator<RecommendationDto>>
            {
                // Give this extra weight
                similarToRecentlyPlayed,
                similarToRecentlyPlayed,

                // Give this extra weight
                similarToLiked,
                similarToLiked,

                hasDirectorFromRecentlyPlayed,
                hasActorFromRecentlyPlayed
            };

            while (categories.Count < categoryLimit)
            {
                var allEmpty = true;

                foreach (var category in categoryTypes)
                {
                    if (category.MoveNext())
                    {
                        categories.Add(category.Current);
                        allEmpty = false;

                        if (categories.Count >= categoryLimit)
                        {
                            break;
                        }
                    }
                }

                if (allEmpty)
                {
                    break;
                }
            }

            return categories.OrderBy(i => i.RecommendationType).ThenBy(i => Guid.NewGuid());
        }

        private IEnumerable<RecommendationDto> GetWithDirector(User user, List<BaseItem> allMovies, IEnumerable<string> directors, int itemLimit, List<ItemFields> fields, RecommendationType type)
        {
            var userId = user.Id;

            foreach (var director in directors)
            {
                var items = allMovies
                    .Where(i => i.People.Any(p => string.Equals(p.Type, PersonType.Director, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Name, director, StringComparison.OrdinalIgnoreCase)))
                    .Take(itemLimit)
                    .ToList();

                if (items.Count > 0)
                {
                    yield return new RecommendationDto
                    {
                        BaselineItemName = director,
                        CategoryId = director.GetMD5().ToString("N"),
                        RecommendationType = type,
                        Items = items.Select(i => _dtoService.GetBaseItemDto(i, fields, user)).ToArray()
                    };
                }
            }
        }

        private IEnumerable<RecommendationDto> GetWithActor(User user, List<BaseItem> allMovies, IEnumerable<string> names, int itemLimit, List<ItemFields> fields, RecommendationType type)
        {
            var userId = user.Id;

            foreach (var name in names)
            {
                var items = allMovies
                    .Where(i => i.People.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .Take(itemLimit)
                    .ToList();

                if (items.Count > 0)
                {
                    yield return new RecommendationDto
                    {
                        BaselineItemName = name,
                        CategoryId = name.GetMD5().ToString("N"),
                        RecommendationType = type,
                        Items = items.Select(i => _dtoService.GetBaseItemDto(i, fields, user)).ToArray()
                    };
                }
            }
        }

        private IEnumerable<RecommendationDto> GetSimilarTo(User user, List<BaseItem> allMovies, IEnumerable<BaseItem> baselineItems, int itemLimit, List<ItemFields> fields, RecommendationType type)
        {
            var userId = user.Id;

            foreach (var item in baselineItems)
            {
                var similar = SimilarItemsHelper
                    .GetSimilaritems(item, allMovies, SimilarItemsHelper.GetSimiliarityScore)
                    .Take(itemLimit)
                    .ToList();

                if (similar.Count > 0)
                {
                    yield return new RecommendationDto
                    {
                        BaselineItemName = item.Name,
                        CategoryId = item.Id.ToString("N"),
                        RecommendationType = type,
                        Items = similar.Select(i => _dtoService.GetBaseItemDto(i, fields, user)).ToArray()
                    };
                }
            }
        }

        private IEnumerable<string> GetActors(IEnumerable<BaseItem> items)
        {
            // Get the two leading actors for all movies
            return items
                .SelectMany(i => i.People.Where(p => !string.Equals(PersonType.Director, p.Type, StringComparison.OrdinalIgnoreCase)).Take(2))
                .Select(i => i.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<string> GetDirectors(IEnumerable<BaseItem> items)
        {
            return items
                .Select(i => i.People.FirstOrDefault(p => string.Equals(PersonType.Director, p.Type, StringComparison.OrdinalIgnoreCase)))
                .Where(i => i != null)
                .Select(i => i.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
