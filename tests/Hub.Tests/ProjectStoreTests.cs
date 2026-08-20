using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OneRemoteCli.Hub.Projects;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The hub's second piece of durable state, after <c>OperatorStateStore</c> - see
/// <see cref="ProjectStore"/>'s own remarks for why it is a file at all, and why
/// every mutation flushes immediately instead of on a timer.
/// </summary>
public class ProjectStoreTests
{
    private const string UserA = "github:alice";
    private const string UserB = "github:bob";
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0];

    [Fact]
    public void EveryUserAlwaysHasAGeneralProject()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            ProjectInfo[] projects = store.List(UserA);

            ProjectInfo general = Assert.Single(projects);
            Assert.True(general.IsGeneral);
            Assert.Equal(ProjectStore.GeneralProjectId, general.ProjectId);
            Assert.Equal(ProjectStore.GeneralProjectName, general.Name);
        });
    }

    [Fact]
    public void GeneralIsAlwaysFirstEvenWhenItWasNotTheFirstCreated()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            Assert.True(store.TryCreate(UserA, "Alpha", null, null, null, out _, out _));

            ProjectInfo[] projects = store.List(UserA);

            Assert.Equal(2, projects.Length);
            Assert.True(projects[0].IsGeneral);
            Assert.Equal("Alpha", projects[1].Name);
        });
    }

    [Fact]
    public void GeneralCannotBeDeleted()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            store.List(UserA); // seeds General

            Assert.False(store.TryDelete(UserA, ProjectStore.GeneralProjectId, out string? error));
            Assert.Equal(ErrorCodes.CannotDeleteGeneralProject, error);
        });
    }

    [Fact]
    public void GeneralCannotBeRenamedButItsMetadataCanBeEdited()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            store.List(UserA); // seeds General

            Assert.False(store.TryUpdate(
                UserA, ProjectStore.GeneralProjectId, "Everything", "catch-all", null, null,
                out ProjectInfo? renamed, out string? renameError));

            Assert.Null(renamed);
            Assert.Equal(ErrorCodes.InvalidRequest, renameError);

            Assert.True(store.TryUpdate(
                UserA, ProjectStore.GeneralProjectId, ProjectStore.GeneralProjectName, "catch-all", null, null,
                out ProjectInfo? project, out string? error));

            Assert.Null(error);
            Assert.Equal(ProjectStore.GeneralProjectName, project!.Name);
            Assert.Equal("catch-all", project.Description);
            Assert.True(project.IsGeneral);
        });
    }

    [Fact]
    public void ANameIsRequiredAndCannotExceedTheLimit()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            Assert.False(store.TryCreate(UserA, "   ", null, null, null, out _, out string? blankError));
            Assert.Equal(ErrorCodes.InvalidRequest, blankError);

            Assert.False(store.TryCreate(UserA, new string('x', 61), null, null, null, out _, out string? longError));
            Assert.Equal(ErrorCodes.InvalidRequest, longError);
        });
    }

    [Fact]
    public void NamesAreUniquePerUserCaseInsensitively()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            Assert.True(store.TryCreate(UserA, "Website", null, null, null, out _, out _));
            Assert.False(store.TryCreate(UserA, "WEBSITE", null, null, null, out _, out string? error));
            Assert.Equal(ErrorCodes.DuplicateProjectName, error);
        });
    }

    [Fact]
    public void ANameCannotCollideWithGeneralEitherByCase()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            Assert.False(store.TryCreate(UserA, "general", null, null, null, out _, out string? error));
            Assert.Equal(ErrorCodes.DuplicateProjectName, error);
        });
    }

    [Fact]
    public void UpdatingAProjectToItsOwnUnchangedNameIsNotADuplicate()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            store.TryCreate(UserA, "Website", "old", null, null, out ProjectInfo? created, out _);

            Assert.True(store.TryUpdate(
                UserA, created!.ProjectId, "Website", "new", null, null, out ProjectInfo? updated, out string? error));

            Assert.Null(error);
            Assert.Equal("new", updated!.Description);
        });
    }

    [Fact]
    public void OnlyAbsoluteHttpUrlsAreAccepted()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            Assert.False(store.TryCreate(
                UserA, "Website", null, "not-a-url", null, out _, out string? error));
            Assert.Equal(ErrorCodes.InvalidProjectSiteUrl, error);

            Assert.False(store.TryCreate(
                UserA, "Website", null, null, "github.com/o/r", out _, out string? repoError));
            Assert.Equal(ErrorCodes.InvalidProjectRepoUrl, repoError);

            Assert.True(store.TryCreate(
                UserA, "Website", null, "https://example.com", "https://github.com/o/r", out ProjectInfo? project, out _));
            Assert.Equal("https://example.com", project!.SiteUrl);
        });
    }

    [Fact]
    public void UpdatingAMissingProjectFails()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            Assert.False(store.TryUpdate(
                UserA, "does-not-exist", "Name", null, null, null, out ProjectInfo? project, out string? error));

            Assert.Null(project);
            Assert.Equal(ErrorCodes.ProjectNotFound, error);
        });
    }

    [Fact]
    public void DeletingAMissingProjectFails()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            Assert.False(store.TryDelete(UserA, "does-not-exist", out string? error));
            Assert.Equal(ErrorCodes.ProjectNotFound, error);
        });
    }

    /// <summary>Ownership isolation: two users' projects never collide or leak across the partition boundary.</summary>
    [Fact]
    public void UsersCannotSeeEachOthersProjects()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);

            store.TryCreate(UserA, "Alpha", null, null, null, out ProjectInfo? aliceProject, out _);
            store.TryCreate(UserB, "Alpha", null, null, null, out ProjectInfo? bobProject, out _);

            // Same name is fine across users - uniqueness is per-user, not global.
            Assert.NotEqual(aliceProject!.ProjectId, bobProject!.ProjectId);

            Assert.False(store.Exists(UserB, aliceProject.ProjectId));
            Assert.False(store.TryDelete(UserB, aliceProject.ProjectId, out string? error));
            Assert.Equal(ErrorCodes.ProjectNotFound, error);

            Assert.Single(store.List(UserA), p => !p.IsGeneral);
            Assert.Single(store.List(UserB), p => !p.IsGeneral);
        });
    }

    [Fact]
    public void WhatWasWrittenComesBackAfterARestart()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore first = Open(path);
            first.TryCreate(UserA, "Website", "desc", "https://example.com", "https://github.com/o/r",
                out ProjectInfo? created, out _);

            ProjectStore second = Open(path);
            ProjectInfo restored = Assert.Single(second.List(UserA), p => !p.IsGeneral);

            Assert.Equal(created!.ProjectId, restored.ProjectId);
            Assert.Equal("Website", restored.Name);
            Assert.Equal("desc", restored.Description);
            Assert.Equal("https://example.com", restored.SiteUrl);
            Assert.Equal("https://github.com/o/r", restored.RepoUrl);
        });
    }

    [Fact]
    public void SessionProjectAssignmentsComeBackAfterARestartAndCanBeCleared()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore first = Open(path);
            Assert.True(first.TryCreate(
                UserA, "Website", null, null, null, out ProjectInfo? project, out _));
            Assert.True(first.TrySetSessionProject(
                UserA, "machine-a", "session-1", project!.ProjectId, out string? setError));
            Assert.Null(setError);

            ProjectStore second = Open(path);
            Assert.Equal(
                project.ProjectId,
                second.ProjectOfSession(UserA, "machine-a", "session-1"));

            Assert.True(second.TrySetSessionProject(
                UserA, "machine-a", "session-1", projectId: null, out string? clearError));
            Assert.Null(clearError);
            Assert.Null(Open(path).ProjectOfSession(UserA, "machine-a", "session-1"));
        });
    }

    [Fact]
    public void DeletingAProjectDeletesItsDurableSessionAssignments()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? project, out _);
            store.TrySetSessionProject(
                UserA, "machine-a", "session-1", project!.ProjectId, out _);

            Assert.True(store.TryDelete(UserA, project.ProjectId, out string? error));

            Assert.Null(error);
            Assert.Null(Open(path).ProjectOfSession(UserA, "machine-a", "session-1"));
        });
    }

    /// <summary>Starting fresh beats refusing to start - the same contract as <c>OperatorStateStore</c>.</summary>
    [Fact]
    public void AFileThatCannotBeParsedIsNotFatal()
    {
        Use((path, iconRoot) =>
        {
            File.WriteAllText(path, "{ this is not json");

            ProjectStore store = Open(path);
            ProjectInfo general = Assert.Single(store.List(UserA));
            Assert.True(general.IsGeneral);

            string directory = Path.GetDirectoryName(path)!;
            string pattern = Path.GetFileName(path) + ".corrupt-*";
            Assert.Single(Directory.GetFiles(directory, pattern));
        });
    }

    [Fact]
    public void AMissingFileIsAnEmptyStateRatherThanAnError()
    {
        Use((path, iconRoot) => Assert.Single(Open(path).List(UserA)));
    }

    [Fact]
    public void NothingIsLeftBehindByAWrite()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            store.TryCreate(UserA, "Website", null, null, null, out _, out _);

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        });
    }

    [Fact]
    public async Task ConcurrentMutationsAreAllPersisted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}.json");

        try
        {
            ProjectStore store = Open(path);
            Task<bool>[] creates = Enumerable.Range(0, 50)
                .Select(index => Task.Run(() =>
                    store.TryCreate(UserA, $"Project {index}", null, null, null, out _, out _)))
                .ToArray();

            Assert.All(await Task.WhenAll(creates), Assert.True);

            ProjectInfo[] restored = Open(path).List(UserA);
            Assert.Equal(51, restored.Length);
            Assert.Equal(50, restored.Select(project => project.Name).Distinct().Count() - 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWriteFailureFailsTheMutationAndDisablesLaterMutations()
    {
        string blockingFile = Path.Combine(Path.GetTempPath(), $"projects-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a directory");

        try
        {
            ProjectStore store = Open(Path.Combine(blockingFile, "project-state.json"));

            Assert.False(store.TryCreate(
                UserA, "First", null, null, null, out ProjectInfo? first, out string? firstError));
            Assert.Null(first);
            Assert.Equal(ErrorCodes.InternalError, firstError);
            Assert.DoesNotContain(store.List(UserA), project => project.Name == "First");

            Assert.False(store.TryCreate(
                UserA, "Second", null, null, null, out ProjectInfo? second, out string? secondError));
            Assert.Null(second);
            Assert.Equal(ErrorCodes.InternalError, secondError);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public void AFailedUpdateRestoresThePersistedValues()
    {
        UseWithStateFailure((store, original) =>
        {
            Assert.False(store.TryUpdate(
                UserA, original.ProjectId, "Changed", "changed", null, null,
                out ProjectInfo? updated, out string? error));

            Assert.Null(updated);
            Assert.Equal(ErrorCodes.InternalError, error);
            Assert.True(store.TryGet(UserA, original.ProjectId, out ProjectInfo? restored));
            Assert.Equal(original.Name, restored!.Name);
            Assert.Equal(original.Description, restored.Description);
        });
    }

    [Fact]
    public void AFailedDeleteRestoresTheProject()
    {
        UseWithStateFailure((store, original) =>
        {
            Assert.False(store.TryDelete(UserA, original.ProjectId, out string? error));

            Assert.Equal(ErrorCodes.InternalError, error);
            Assert.True(store.Exists(UserA, original.ProjectId));
        });
    }

    [Fact]
    public void AFailedIconUpdateRestoresThePreviousIconState()
    {
        UseWithStateFailure((store, original) =>
        {
            Assert.False(store.TrySetIcon(
                UserA, original.ProjectId, Png, "image/png",
                out ProjectInfo? updated, out string? error));

            Assert.Null(updated);
            Assert.Equal(ErrorCodes.InternalError, error);
            Assert.True(store.TryGet(UserA, original.ProjectId, out ProjectInfo? restored));
            Assert.Equal(0, restored!.IconVersion);
            Assert.False(store.TryReadIcon(UserA, original.ProjectId, out _, out _));
        });
    }

    [Fact]
    public void AFailedIconClearRestoresThePreviousIcon()
    {
        UseWithStateFailure((store, original) =>
        {
            Assert.False(store.TryClearIcon(
                UserA, original.ProjectId, out ProjectInfo? cleared, out string? error));

            Assert.Null(cleared);
            Assert.Equal(ErrorCodes.InternalError, error);
            Assert.True(store.TryGet(UserA, original.ProjectId, out ProjectInfo? restored));
            Assert.Equal(1, restored!.IconVersion);
            Assert.True(store.TryReadIcon(UserA, original.ProjectId, out byte[]? bytes, out string? contentType));
            Assert.Equal(Png, bytes);
            Assert.Equal("image/png", contentType);
        }, (store, original) =>
        {
            Assert.True(store.TrySetIcon(
                UserA, original.ProjectId, Png, "image/png", out _, out _));
        });
    }

    [Fact]
    public void SettingAnIconBumpsTheVersionAndCanBeReadBack()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);

            byte[] bytes = Png;
            Assert.True(store.TrySetIcon(
                UserA, created!.ProjectId, bytes, "image/png", out ProjectInfo? updated, out string? error));

            Assert.Null(error);
            Assert.Equal(1, updated!.IconVersion);

            Assert.True(store.TryReadIcon(UserA, created.ProjectId, out byte[]? read, out string? contentType));
            Assert.Equal(bytes, read);
            Assert.Equal("image/png", contentType);
        });
    }

    [Fact]
    public void SettingAnIconTwiceBumpsTheVersionAgain()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);

            store.TrySetIcon(UserA, created!.ProjectId, Png, "image/png", out ProjectInfo? first, out _);
            store.TrySetIcon(UserA, created.ProjectId, Jpeg, "image/jpeg", out ProjectInfo? second, out _);

            Assert.Equal(1, first!.IconVersion);
            Assert.Equal(2, second!.IconVersion);
        });
    }

    [Fact]
    public void ClearingAnIconResetsTheVersionToZeroAndRemovesTheFile()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);
            store.TrySetIcon(UserA, created!.ProjectId, Png, "image/png", out _, out _);

            Assert.True(store.TryClearIcon(UserA, created.ProjectId, out ProjectInfo? cleared, out string? error));

            Assert.Null(error);
            Assert.Equal(0, cleared!.IconVersion);
            Assert.False(store.TryReadIcon(UserA, created.ProjectId, out _, out _));
        });
    }

    [Fact]
    public void AnIconTooLargeIsRejected()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);

            byte[] tooBig = new byte[ProjectStore.MaxIconBytes + 1];
            Assert.False(store.TrySetIcon(
                UserA, created!.ProjectId, tooBig, "image/png", out ProjectInfo? project, out string? error));

            Assert.Null(project);
            Assert.Equal(ErrorCodes.InvalidRequest, error);
        });
    }

    [Fact]
    public void AnUnsupportedContentTypeIsRejected()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);

            Assert.False(store.TrySetIcon(
                UserA, created!.ProjectId, new byte[] { 1 }, "image/gif", out ProjectInfo? project, out string? error));

            Assert.Null(project);
            Assert.Equal(ErrorCodes.InvalidRequest, error);
        });
    }

    [Fact]
    public void AClaimedImageWhoseSignatureDoesNotMatchIsRejected()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);

            Assert.False(store.TrySetIcon(
                UserA, created!.ProjectId, new byte[] { 1, 2, 3, 4 }, "image/png",
                out ProjectInfo? project, out string? error));

            Assert.Null(project);
            Assert.Equal(ErrorCodes.InvalidRequest, error);
        });
    }

    [Fact]
    public void AnIconForAnotherUsersProjectCannotBeRead()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);
            store.TrySetIcon(UserA, created!.ProjectId, Png, "image/png", out _, out _);

            Assert.False(store.TryReadIcon(UserB, created.ProjectId, out _, out _));
        });
    }

    [Fact]
    public void DeletingAProjectRemovesItsIconFile()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);
            store.TrySetIcon(UserA, created!.ProjectId, Png, "image/png", out _, out _);

            Assert.True(store.TryDelete(UserA, created.ProjectId, out _));

            // The project is gone, so its icon can never be served again - no
            // assertion on the file itself, since removal is best-effort cleanup.
            Assert.False(store.Exists(UserA, created.ProjectId));
        });
    }

    private static ProjectStore Open(string path, string? iconRoot = null) => new(
        Options.Create(new ProjectsOptions { StatePath = path, IconRoot = iconRoot ?? string.Empty }),
        TimeProvider.System,
        NullLogger<ProjectStore>.Instance);

    private static void Use(Action<string, string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}.json");
        string iconRoot = Path.Combine(Path.GetTempPath(), $"project-icons-{Guid.NewGuid():N}");

        try
        {
            test(path, iconRoot);
        }
        finally
        {
            File.Delete(path);

            foreach (string leftover in Directory.GetFiles(
                         Path.GetDirectoryName(path)!,
                         Path.GetFileName(path) + ".corrupt-*"))
            {
                File.Delete(leftover);
            }

            if (Directory.Exists(iconRoot))
            {
                Directory.Delete(iconRoot, recursive: true);
            }
        }
    }

    private static void UseWithStateFailure(
        Action<ProjectStore, ProjectInfo> test,
        Action<ProjectStore, ProjectInfo>? beforeFailure = null)
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), $"projects-state-{Guid.NewGuid():N}");
        string path = Path.Combine(stateDirectory, "project-state.json");
        string iconRoot = Path.Combine(Path.GetTempPath(), $"project-icons-{Guid.NewGuid():N}");

        Directory.CreateDirectory(stateDirectory);

        try
        {
            ProjectStore store = Open(path, iconRoot);
            Assert.True(store.TryCreate(
                UserA, "Original", "original", null, null, out ProjectInfo? original, out _));

            beforeFailure?.Invoke(store, original!);
            Directory.Delete(stateDirectory, recursive: true);
            File.WriteAllText(stateDirectory, "not a directory");
            test(store, original!);
        }
        finally
        {
            File.Delete(stateDirectory);

            if (Directory.Exists(iconRoot))
            {
                Directory.Delete(iconRoot, recursive: true);
            }
        }
    }
}
