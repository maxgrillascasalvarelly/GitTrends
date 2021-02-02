﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using GitTrends.Shared;
using Newtonsoft.Json;
using Xamarin.Essentials.Interfaces;

namespace GitTrends
{
    public class GitTrendsStatisticsService
    {
        readonly IPreferences _preferences;
        readonly IAnalyticsService _analyticsService;
        readonly GitHubApiV3Service _gitHubApiV3Service;
        readonly ImageCachingService _imageCachingService;

        public GitTrendsStatisticsService(IPreferences preferences,
                                            IAnalyticsService analyticsService,
                                            GitHubApiV3Service gitHubApiV3Service,
                                            ImageCachingService imageCachingService)
        {
            _preferences = preferences;
            _analyticsService = analyticsService;
            _gitHubApiV3Service = gitHubApiV3Service;
            _imageCachingService = imageCachingService;
        }

        public long? Stars
        {
            get
            {
                var stars = _preferences.Get(nameof(Stars), (long)-1);
                return stars < 0 ? null : stars;
            }
            private set => _preferences.Set(nameof(Stars), value ?? -1);
        }

        public long? Watchers
        {
            get
            {
                var watchers = _preferences.Get(nameof(Watchers), (long)-1);
                return watchers < 0 ? null : watchers;
            }
            private set => _preferences.Set(nameof(Watchers), value ?? -1);
        }

        public long? Forks
        {
            get
            {
                var forks = _preferences.Get(nameof(Forks), (long)-1);
                return forks < 0 ? null : forks;
            }
            private set => _preferences.Set(nameof(Forks), value ?? -1);
        }

        public IReadOnlyList<Contributor> Contributors
        {
            get => GetContributors();
            private set => _preferences.Set(nameof(Contributors), JsonConvert.SerializeObject(value));
        }

        public async ValueTask Initialize(CancellationToken cancellationToken)
        {
            if (Stars is null || Watchers is null || Forks is null || !Contributors.Any())
                await initialize().ConfigureAwait(false);
            else
                initialize().SafeFireAndForget(ex => _analyticsService.Report(ex));

            async Task initialize()
            {
                var getRepositoryTask = _gitHubApiV3Service.GetRepository(GitHubConstants.GitTrendsRepoOwner, GitHubConstants.GitTrendsRepoName, cancellationToken);
                var getGitTrendsContributorsTask = _gitHubApiV3Service.GetGitTrendsContributors(cancellationToken);

                await Task.WhenAll(getRepositoryTask, getGitTrendsContributorsTask).ConfigureAwait(false);

                var gitTrendsRepository = await getRepositoryTask.ConfigureAwait(false);

                Stars = gitTrendsRepository?.StarCount;
                Forks = gitTrendsRepository?.ForkCount;
                Watchers = gitTrendsRepository?.WatchersCount;

                Contributors = await getGitTrendsContributorsTask.ConfigureAwait(false);
                foreach (var contributor in Contributors)
                    _imageCachingService.PreloadImage(contributor.AvatarUrl).SafeFireAndForget(ex => _analyticsService.Report(ex));
            }
        }

        IReadOnlyList<Contributor> GetContributors()
        {
            try
            {
                var contributors = JsonConvert.DeserializeObject<IReadOnlyList<Contributor>>(_preferences.Get(nameof(Contributors), null));
                return contributors ?? Array.Empty<Contributor>();
            }
            catch (ArgumentNullException)
            {
                return Array.Empty<Contributor>();
            }
            catch (JsonReaderException)
            {
                return Array.Empty<Contributor>();
            }
        }
    }
}
