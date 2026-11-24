#define _DARWIN_C_SOURCE
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>
#include "VerifierHashes.h"

// The system verifier checks the suspended process itself. It inherits no
// database or request descriptor and can be terminated even inside Security.
int TimesStartImageCheck(int subject, int *pid)
{
    if (subject <= 0) return EINVAL;
    char target[32];
    snprintf(target, sizeof(target), "%d", subject);
    char *argv[] = {"/usr/bin/codesign", "--verify",
        "-R=cdhash H\"" TIMES_VIEW_ARM64 "\" or cdhash H\"" TIMES_VIEW_X86_64 "\"", target, NULL};
    char *env[] = {NULL};
    posix_spawnattr_t attr;
    posix_spawn_file_actions_t io;
    int rc = posix_spawnattr_init(&attr);
    if (rc) return rc;
    rc = posix_spawn_file_actions_init(&io);
    if (!rc) {
        rc = posix_spawnattr_setflags(&attr, POSIX_SPAWN_CLOEXEC_DEFAULT);
        if (!rc) rc = posix_spawn_file_actions_addopen(&io, STDIN_FILENO, "/dev/null", O_RDONLY, 0);
        if (!rc) rc = posix_spawn_file_actions_addopen(&io, STDOUT_FILENO, "/dev/null", O_WRONLY, 0);
        if (!rc) rc = posix_spawn_file_actions_addopen(&io, STDERR_FILENO, "/dev/null", O_WRONLY, 0);
        if (!rc) rc = posix_spawn(pid, argv[0], &io, &attr, argv, env);
        posix_spawn_file_actions_destroy(&io);
    }
    posix_spawnattr_destroy(&attr);
    return rc;
}

// START_SUSPENDED stops before the first userspace instruction, including dyld.
// No installed-path decision can authorize a different image to run. Descriptor
// 3 pins the parent's database revision for both modes of the metadata helper.
int TimesStartVerifier(const char *path, const char *database, const char *service, const char *account,
                       int presence, int pinned, int *pid, int *request)
{
    struct stat file;
    if (!path || lstat(path, &file) || !S_ISREG(file.st_mode) || pinned < 0) return EINVAL;
    // The caller may have closed standard input or opened its database as fd 3.
    // Keep the source above all four child destinations while installing them.
    int databaseFd = fcntl(pinned, F_DUPFD_CLOEXEC, 4);
    if (databaseFd < 0) return errno;
    int pair[2];
    if (socketpair(AF_UNIX, SOCK_STREAM, 0, pair)) {
        int error = errno;
        close(databaseFd);
        return error;
    }
    int one = 1;
    int rc = fcntl(pair[0], F_SETFD, FD_CLOEXEC) || fcntl(pair[1], F_SETFD, FD_CLOEXEC)
        || setsockopt(pair[0], SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof(one)) ? errno : 0;
    posix_spawnattr_t attr;
    posix_spawn_file_actions_t io;
    bool haveAttr = false, haveIO = false;
    if (!rc) { rc = posix_spawnattr_init(&attr); haveAttr = !rc; }
    if (!rc) { rc = posix_spawn_file_actions_init(&io); haveIO = !rc; }
    if (!rc) rc = posix_spawnattr_setflags(&attr, POSIX_SPAWN_START_SUSPENDED | POSIX_SPAWN_CLOEXEC_DEFAULT);
    if (!rc) rc = pair[1] == STDIN_FILENO ? posix_spawn_file_actions_addinherit_np(&io, STDIN_FILENO)
                                          : posix_spawn_file_actions_adddup2(&io, pair[1], STDIN_FILENO);
    if (!rc) rc = posix_spawn_file_actions_adddup2(&io, databaseFd, 3);
    if (!rc) rc = posix_spawn_file_actions_addopen(&io, STDOUT_FILENO, "/dev/null", O_WRONLY, 0);
    if (!rc) rc = posix_spawn_file_actions_addopen(&io, STDERR_FILENO, "/dev/null", O_WRONLY, 0);
    char *argv[] = {(char *)path, (char *)database, (char *)service, (char *)account,
                    presence ? "presence" : "view", NULL};
    char *env[] = {NULL};
    if (!rc) rc = posix_spawn(pid, path, &io, &attr, argv, env);
    if (haveIO) posix_spawn_file_actions_destroy(&io);
    if (haveAttr) posix_spawnattr_destroy(&attr);
    close(databaseFd);
    close(pair[1]);
    if (rc) close(pair[0]);
    else *request = pair[0];
    return rc;
}

int TimesWaitVerifier(int pid)
{
    // Leave the child waitable until the cancellation callback is unregistered.
    // Its PID therefore cannot be reused while the callback can still signal it.
    siginfo_t info = {0};
    int rc;
    do { rc = waitid(P_PID, (id_t)pid, &info, WEXITED | WNOWAIT); } while (rc && errno == EINTR);
    return !rc && info.si_code == CLD_EXITED ? info.si_status : 1;
}

int TimesResumeVerifier(int pid, int fd, const uint8_t *request, int size)
{
    if (kill(pid, SIGCONT) || send(fd, request, size, 0) != size || shutdown(fd, SHUT_WR)) return 1;
    return TimesWaitVerifier(pid);
}

void TimesStopVerifier(int pid)
{
    siginfo_t state = {0};
    int rc;
    do { rc = waitid(P_PID, (id_t)pid, &state, WEXITED | WNOWAIT | WNOHANG); } while (rc && errno == EINTR);
    if (!rc && !state.si_pid) kill(pid, SIGKILL);
}

void TimesReleaseVerifier(int pid, int fd)
{
    if (fd >= 0) close(fd);
    TimesStopVerifier(pid);
    while (waitpid(pid, NULL, 0) < 0 && errno == EINTR) {}
}
