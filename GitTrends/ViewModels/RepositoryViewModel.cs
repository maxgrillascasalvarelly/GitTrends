﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AsyncAwaitBestPractices;
using AsyncAwaitBestPractices.MVVM;
using Autofac;
using GitTrends.Mobile.Common;
using GitTrends.Mobile.Common.Constants;
using GitTrends.Shared;
using Refit;
using Xamarin.Essentials.Interfaces;
using Xamarin.Forms;

namespace GitTrends
{
    public class RepositoryViewModel : BaseViewModel
    {
        readonly static WeakEventManager<PullToRefreshFailedEventArgs> _pullToRefreshFailedEventManager = new WeakEventManager<PullToRefreshFailedEventArgs>();

        readonly ImageCachingService _imageService;
        readonly GitHubUserService _gitHubUserService;
        readonly RepositoryDatabase _repositoryDatabase;
        readonly GitHubApiV3Service _gitHubApiV3Service;
        readonly MobileSortingService _mobileSortingService;
        readonly GitHubGraphQLApiService _gitHubGraphQLApiService;
        readonly GitHubApiExceptionService _gitHubApiExceptionService;
        readonly GitHubAuthenticationService _gitHubAuthenticationService;
        readonly GitHubApiRepositoriesService _gitHubApiRepositoriesService;

        bool _isRefreshing;
        string _titleText = string.Empty;
        string _searchBarText = string.Empty;
        string _emptyDataViewTitle = string.Empty;
        string _emptyDataViewDescription = string.Empty;

        IReadOnlyList<Repository> _repositoryList = Array.Empty<Repository>();
        IReadOnlyList<Repository> _visibleRepositoryList = Array.Empty<Repository>();

        public RepositoryViewModel(IMainThread mainThread,
                                    ImageCachingService imageService,
                                    IAnalyticsService analyticsService,
                                    GitHubUserService gitHubUserService,
                                    MobileSortingService sortingService,
                                    RepositoryDatabase repositoryDatabase,
                                    GitHubApiV3Service gitHubApiV3Service,
                                    GitHubGraphQLApiService gitHubGraphQLApiService,
                                    GitHubApiExceptionService gitHubApiExceptionService,
                                    GitHubAuthenticationService gitHubAuthenticationService,
                                    GitHubApiRepositoriesService gitHubApiRepositoriesService) : base(analyticsService, mainThread)
        {
            LanguageService.PreferredLanguageChanged += HandlePreferredLanguageChanged;

            SetTitleText();

            _imageService = imageService;
            _gitHubUserService = gitHubUserService;
            _mobileSortingService = sortingService;
            _repositoryDatabase = repositoryDatabase;
            _gitHubApiV3Service = gitHubApiV3Service;
            _gitHubGraphQLApiService = gitHubGraphQLApiService;
            _gitHubApiExceptionService = gitHubApiExceptionService;
            _gitHubAuthenticationService = gitHubAuthenticationService;
            _gitHubApiRepositoriesService = gitHubApiRepositoriesService;

            RefreshState = RefreshState.Uninitialized;

            FilterRepositoriesCommand = new Command<string>(SetSearchBarText);
            SortRepositoriesCommand = new Command<SortingOption>(ExecuteSortRepositoriesCommand);
            PullToRefreshCommand = new AsyncCommand(() => ExecutePullToRefreshCommand(gitHubUserService.Alias));
            ToggleIsFavoriteCommand = new AsyncCommand<Repository>(repository => ExecuteToggleIsFavoriteCommand(repository));

            NotificationService.SortingOptionRequested += HandleSortingOptionRequested;

            GitHubAuthenticationService.DemoUserActivated += HandleDemoUserActivated;
            GitHubAuthenticationService.LoggedOut += HandleGitHubAuthenticationServiceLoggedOut;
            GitHubAuthenticationService.AuthorizeSessionCompleted += HandleAuthorizeSessionCompleted;
        }

        public static event EventHandler<PullToRefreshFailedEventArgs> PullToRefreshFailed
        {
            add => _pullToRefreshFailedEventManager.AddEventHandler(value);
            remove => _pullToRefreshFailedEventManager.RemoveEventHandler(value);
        }

        public ICommand SortRepositoriesCommand { get; }
        public ICommand FilterRepositoriesCommand { get; }
        public IAsyncCommand PullToRefreshCommand { get; }
        public IAsyncCommand<Repository> ToggleIsFavoriteCommand { get; }

        public IReadOnlyList<Repository> VisibleRepositoryList
        {
            get => _visibleRepositoryList;
            set => SetProperty(ref _visibleRepositoryList, value);
        }

        public string EmptyDataViewTitle
        {
            get => _emptyDataViewTitle;
            set => SetProperty(ref _emptyDataViewTitle, value);
        }

        public string EmptyDataViewDescription
        {
            get => _emptyDataViewDescription;
            set => SetProperty(ref _emptyDataViewDescription, value);
        }

        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        public string TitleText
        {
            get => _titleText;
            set => SetProperty(ref _titleText, value);
        }

        RefreshState RefreshState
        {
            set
            {
                EmptyDataViewTitle = EmptyDataViewService.GetRepositoryTitleText(value, !_repositoryList.Any());
                EmptyDataViewDescription = EmptyDataViewService.GetRepositoryDescriptionText(value, !_repositoryList.Any());
            }
        }

        async Task ExecutePullToRefreshCommand(string repositoryOwner)
        {
            HttpResponseMessage? finalResponse = null;

            var cancellationTokenSource = new CancellationTokenSource();
            GitHubAuthenticationService.LoggedOut += HandleLoggedOut;
            GitHubAuthenticationService.AuthorizeSessionStarted += HandleAuthorizeSessionStarted;

            AnalyticsService.Track("Refresh Triggered", "Sorting Option", _mobileSortingService.CurrentOption.ToString());

            try
            {
                const int minimumBatchCount = 20;

                var favoriteRepositoryUrls = await _repositoryDatabase.GetFavoritesUrls().ConfigureAwait(false);

                var repositoryList = new List<Repository>();
                await foreach (var repository in _gitHubGraphQLApiService.GetRepositories(repositoryOwner, cancellationTokenSource.Token).ConfigureAwait(false))
                {
                    if (favoriteRepositoryUrls.Contains(repository.Url))
                        repositoryList.Add(new Repository(repository.Name, repository.Description, repository.ForkCount, repository.OwnerLogin, repository.OwnerAvatarUrl, repository.IssuesCount, repository.Url, repository.IsFork, repository.DataDownloadedAt, true));
                    else
                        repositoryList.Add(repository);

                    //Batch the VisibleRepositoryList Updates to avoid overworking the UI Thread
                    if (!_gitHubUserService.IsDemoUser && repositoryList.Count > minimumBatchCount)
                    {
                        //Only display the first update to avoid unncessary work on the UIThread
                        var shouldUpdateVisibleRepositoryList = !VisibleRepositoryList.Any();
                        AddRepositoriesToCollection(repositoryList, _searchBarText, shouldUpdateVisibleRepositoryList);
                        repositoryList.Clear();
                    }
                }

                //Add Remaining Repositories to _repositoryList
                AddRepositoriesToCollection(repositoryList, _searchBarText);

                var completedRepositories = new List<Repository>();
                await foreach (var retrievedRepositoryWithViewsAndClonesData in _gitHubApiRepositoriesService.UpdateRepositoriesWithViewsClonesAndStarsData(_repositoryList, cancellationTokenSource.Token).ConfigureAwait(false))
                {
                    completedRepositories.Add(retrievedRepositoryWithViewsAndClonesData);

                    //Batch the VisibleRepositoryList Updates to avoid overworking the UI Thread
                    if (!_gitHubUserService.IsDemoUser && completedRepositories.Count > minimumBatchCount)
                    {
                        AddRepositoriesToCollection(completedRepositories, _searchBarText);
                        completedRepositories.Clear();
                    }
                }

                //Add Remaining Repositories to VisibleRepositoryList
                AddRepositoriesToCollection(completedRepositories, _searchBarText, true);

                if (!_gitHubUserService.IsDemoUser)
                {
                    //Call EnsureSuccessStatusCode to confirm the above API calls executed successfully
                    finalResponse = await _gitHubApiV3Service.GetGitHubApiResponse(cancellationTokenSource.Token).ConfigureAwait(false);
                    finalResponse.EnsureSuccessStatusCode();
                }

                RefreshState = RefreshState.Succeeded;
            }
            catch (Exception e) when ((e is ApiException exception && exception.StatusCode is HttpStatusCode.Unauthorized)
                                        || (e is HttpRequestException && finalResponse != null && finalResponse.StatusCode is HttpStatusCode.Unauthorized))
            {
                var loginExpiredEventArgs = new LoginExpiredPullToRefreshEventArgs();

                OnPullToRefreshFailed(new LoginExpiredPullToRefreshEventArgs());

                await _gitHubAuthenticationService.LogOut().ConfigureAwait(false);
                await _repositoryDatabase.DeleteAllData().ConfigureAwait(false);

                SetRepositoriesCollection(Array.Empty<Repository>(), _searchBarText);

                RefreshState = RefreshState.LoginExpired;
            }
            catch (Exception e) when (_gitHubApiExceptionService.HasReachedMaximimApiCallLimit(e)
                                        || (e is HttpRequestException && finalResponse != null && _gitHubApiExceptionService.HasReachedMaximimApiCallLimit(finalResponse.Headers)))
            {
                var responseHeaders = e switch
                {
                    ApiException exception => exception.Headers,
                    GraphQLException graphQLException => graphQLException.ResponseHeaders,
                    HttpRequestException _ when finalResponse != null => finalResponse.Headers,
                    _ => throw new NotSupportedException()
                };

                var maximimApiRequestsReachedEventArgs = new MaximimApiRequestsReachedEventArgs(_gitHubApiExceptionService.GetRateLimitResetDateTime(responseHeaders));
                OnPullToRefreshFailed(maximimApiRequestsReachedEventArgs);

                SetRepositoriesCollection(Array.Empty<Repository>(), _searchBarText);

                RefreshState = RefreshState.MaximumApiLimit;
            }
            catch (Exception e)
            {
                AnalyticsService.Report(e);

                var repositoryDatabaseList = await _repositoryDatabase.GetRepositories().ConfigureAwait(false);
                SetRepositoriesCollection(repositoryDatabaseList, _searchBarText);

                if (repositoryDatabaseList.Any())
                {
                    var dataDownloadedAt = repositoryDatabaseList.Max(x => x.DataDownloadedAt);
                    OnPullToRefreshFailed(new ErrorPullToRefreshEventArgs($"{RepositoryPageConstants.DisplayingDataFrom} {dataDownloadedAt.ToLocalTime():dd MMMM @ HH:mm}\n\n{RepositoryPageConstants.CheckInternetConnectionTryAgain}."));
                }
                else
                {
                    OnPullToRefreshFailed(new ErrorPullToRefreshEventArgs(RepositoryPageConstants.CheckInternetConnectionTryAgain));
                }

                RefreshState = RefreshState.Error;
            }
            finally
            {
                GitHubAuthenticationService.LoggedOut -= HandleLoggedOut;
                GitHubAuthenticationService.AuthorizeSessionStarted -= HandleAuthorizeSessionStarted;

                if (cancellationTokenSource.IsCancellationRequested)
                    UpdateListForLoggedOutUser();

                IsRefreshing = false;

                SaveRepositoriesToDatabase(_repositoryList).SafeFireAndForget();
            }

            void HandleLoggedOut(object sender, EventArgs e) => cancellationTokenSource.Cancel();
            void HandleAuthorizeSessionStarted(object sender, EventArgs e) => cancellationTokenSource.Cancel();
        }

        Task ExecuteToggleIsFavoriteCommand(Repository repository)
        {
            repository = new Repository(repository.Name, repository.Description, repository.ForkCount, repository.OwnerLogin, repository.OwnerAvatarUrl,
                                            repository.IssuesCount, repository.Url, repository.IsFork, repository.DataDownloadedAt, !repository.IsFavorite,
                                            repository.DailyViewsList.ToArray(), repository.DailyClonesList.ToArray(), repository.StarredAt.ToArray());

            UpdateVisibleRepositoryList(_searchBarText, _mobileSortingService.CurrentOption, _mobileSortingService.IsReversed);

            return _repositoryDatabase.SaveRepository(repository);
        }

        async ValueTask SaveRepositoriesToDatabase(IEnumerable<Repository> repositories)
        {
            if (_gitHubUserService.IsDemoUser)
                return;

            foreach (var repository in repositories)
            {
                try
                {
                    await _repositoryDatabase.SaveRepository(repository).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    AnalyticsService.Report(e);
                }
            }
        }

        void ExecuteSortRepositoriesCommand(SortingOption option)
        {
            if (_mobileSortingService.CurrentOption == option)
                _mobileSortingService.IsReversed = !_mobileSortingService.IsReversed;
            else
                _mobileSortingService.IsReversed = false;

            _mobileSortingService.CurrentOption = option;

            AnalyticsService.Track("SortingOption Changed", new Dictionary<string, string>
            {
                { nameof(MobileSortingService) + nameof(MobileSortingService.CurrentOption), _mobileSortingService.CurrentOption.ToString() },
                { nameof(MobileSortingService) + nameof(MobileSortingService.IsReversed), _mobileSortingService.IsReversed.ToString() }
            });

            UpdateVisibleRepositoryList(_searchBarText, _mobileSortingService.CurrentOption, _mobileSortingService.IsReversed);
        }

        void SetRepositoriesCollection(in IReadOnlyList<Repository> repositories, in string searchBarText)
        {
            _repositoryList = repositories;

            UpdateVisibleRepositoryList(searchBarText, _mobileSortingService.CurrentOption, _mobileSortingService.IsReversed);
        }

        void AddRepositoriesToCollection(in IReadOnlyList<Repository> repositories, in string searchBarText, in bool shouldUpdateVisibleRepositoryList = true, in bool shouldRemoveRepoisitoriesWithoutViewsClonesData = false)
        {
            var updatedRepositoryList = _repositoryList.Concat(repositories);

            if (shouldRemoveRepoisitoriesWithoutViewsClonesData)
                _repositoryList = RepositoryService.RemoveForksAndDuplicates(updatedRepositoryList).Where(x => x.DailyClonesList.Count > 1 || x.DailyViewsList.Count > 1).ToList();
            else
                _repositoryList = RepositoryService.RemoveForksAndDuplicates(updatedRepositoryList).ToList();

            if (shouldUpdateVisibleRepositoryList)
                UpdateVisibleRepositoryList(searchBarText, _mobileSortingService.CurrentOption, _mobileSortingService.IsReversed);
        }

        void UpdateVisibleRepositoryList(in string searchBarText, in SortingOption sortingOption, in bool isReversed)
        {
            var filteredRepositoryList = GetRepositoriesFilteredBySearchBar(_repositoryList, searchBarText);

            VisibleRepositoryList = MobileSortingService.SortRepositories(filteredRepositoryList, sortingOption, isReversed).ToList();

            _imageService.PreloadRepositoryImages(VisibleRepositoryList).SafeFireAndForget(ex => AnalyticsService.Report(ex));
        }

        void UpdateListForLoggedOutUser()
        {
            _repositoryList = Array.Empty<Repository>();
            UpdateVisibleRepositoryList(string.Empty, _mobileSortingService.CurrentOption, _mobileSortingService.IsReversed);
        }

        IEnumerable<Repository> GetRepositoriesFilteredBySearchBar(in IReadOnlyList<Repository> repositories, string searchBarText)
        {
            if (string.IsNullOrWhiteSpace(searchBarText))
                return repositories;

            return repositories.Where(x => x.Name.Contains(searchBarText, StringComparison.OrdinalIgnoreCase));
        }

        void SetSearchBarText(string text)
        {
            if (EqualityComparer<string>.Default.Equals(_searchBarText, text))
                return;

            _searchBarText = text;

            if (_repositoryList.Any())
                UpdateVisibleRepositoryList(_searchBarText, _mobileSortingService.CurrentOption, _mobileSortingService.IsReversed);
        }

        void HandlePreferredLanguageChanged(object sender, string? e) => SetTitleText();

        void SetTitleText() => TitleText = PageTitles.RepositoryPage;

        //Work-around because ContentPage.OnAppearing does not fire after `ContentPage.PushModalAsync()`
        void HandleAuthorizeSessionCompleted(object sender, AuthorizeSessionCompletedEventArgs e) => IsRefreshing |= e.IsSessionAuthorized;

        void HandleDemoUserActivated(object sender, EventArgs e) => IsRefreshing = true;

        void HandleGitHubAuthenticationServiceLoggedOut(object sender, EventArgs e) => UpdateListForLoggedOutUser();

        void HandleSortingOptionRequested(object sender, SortingOption sortingOption) => SortRepositoriesCommand.Execute(sortingOption);

        void OnPullToRefreshFailed(PullToRefreshFailedEventArgs pullToRefreshFailedEventArgs)
        {
            RefreshState = pullToRefreshFailedEventArgs switch
            {
                ErrorPullToRefreshEventArgs _ => RefreshState.Error,
                MaximimApiRequestsReachedEventArgs _ => RefreshState.MaximumApiLimit,
                LoginExpiredPullToRefreshEventArgs _ => RefreshState.LoginExpired,
                _ => throw new NotSupportedException()
            };

            _pullToRefreshFailedEventManager.RaiseEvent(this, pullToRefreshFailedEventArgs, nameof(PullToRefreshFailed));
        }
    }
}
