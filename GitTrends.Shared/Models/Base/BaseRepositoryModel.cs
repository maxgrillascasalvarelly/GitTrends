﻿namespace GitTrends.Shared;

public interface IBaseRepositoryModel
{
	long TotalCount { get; }
	long TotalUniqueCount { get; }
	string RepositoryName { get; }
	string RepositoryOwner { get; }
}