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
    public void GeneralCanBeRenamedAndReDescribed()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path);
            store.List(UserA); // seeds General

            Assert.True(store.TryUpdate(
                UserA, ProjectStore.GeneralProjectId, "Everything", "catch-all", null, null,
                out ProjectInfo? project, out string? error));

            Assert.Null(error);
            Assert.Equal("Everything", project!.Name);
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
            Assert.Equal(ErrorCodes.InvalidRequest, error);

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
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void SettingAnIconBumpsTheVersionAndCanBeReadBack()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);

            byte[] bytes = [1, 2, 3, 4];
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

            store.TrySetIcon(UserA, created!.ProjectId, new byte[] { 1 }, "image/png", out ProjectInfo? first, out _);
            store.TrySetIcon(UserA, created.ProjectId, new byte[] { 2 }, "image/jpeg", out ProjectInfo? second, out _);

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
            store.TrySetIcon(UserA, created!.ProjectId, new byte[] { 1 }, "image/png", out _, out _);

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
    public void AnIconForAnotherUsersProjectCannotBeRead()
    {
        Use((path, iconRoot) =>
        {
            ProjectStore store = Open(path, iconRoot);
            store.TryCreate(UserA, "Website", null, null, null, out ProjectInfo? created, out _);
            store.TrySetIcon(UserA, created!.ProjectId, new byte[] { 1 }, "image/png", out _, out _);

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
            store.TrySetIcon(UserA, created!.ProjectId, new byte[] { 1 }, "image/png", out _, out _);

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
            File.Delete(path + ".tmp");

            if (Directory.Exists(iconRoot))
            {
                Directory.Delete(iconRoot, recursive: true);
            }
        }
    }
}
