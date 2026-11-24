// Local FileDL uses flock on .fl followed by the first four SHA-1 bytes of
// the database's basename. The database and lock are opened without following links.
#define _DARWIN_C_SOURCE
#include <CommonCrypto/CommonDigest.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdio.h>
#include <string.h>
#include <sys/file.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <unistd.h>

static int regularFile(int fd)
{
    struct stat st;
    return fd >= 0 && fstat(fd, &st) == 0 && S_ISREG(st.st_mode) && st.st_nlink == 1;
}

int TimesOpenProbeDatabase(const char *path)
{
    if (!path || path[0] != '/') {
        return -1;
    }
    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    // A FileDL header has five 32-bit fields, starting with its four-byte atom.
    char header[20];
    if (regularFile(fd) && pread(fd, header, sizeof(header), 0) == sizeof(header)
        && memcmp(header, "kych", 4) == 0) {
        return fd;
    }
    if (fd >= 0) {
        close(fd);
    }
    return -1;
}

int TimesDatabaseAtPath(int fd, const char *path)
{
    struct stat pinned, named;
    if (fstat(fd, &pinned) || lstat(path, &named) || !S_ISREG(named.st_mode)
        || pinned.st_nlink != 1 || pinned.st_dev != named.st_dev || pinned.st_ino != named.st_ino) {
        return EINVAL;
    }
    return 0;
}

static void fileLockName(const char *name, char lock[12])
{
    unsigned char digest[CC_SHA1_DIGEST_LENGTH];
    CC_SHA1(name, (CC_LONG)strlen(name), digest);
    snprintf(lock, 12, ".fl%02X%02X%02X%02X", digest[0], digest[1], digest[2], digest[3]);
}

int TimesRemoveScratchLock(int directory, const char *name)
{
    char lock[12];
    fileLockName(name, lock);
    return unlinkat(directory, lock, 0) && errno != ENOENT ? errno : 0;
}

int TimesOpenKeychainLock(const char *path, int lookup, int *result)
{
    *result = -1;
    const char *name = strrchr(path, '/');
    if (path[0] != '/' || !name || !name[1] || name - path >= PATH_MAX) {
        return EINVAL;
    }
    char parent[PATH_MAX];
    size_t length = (size_t)(name - path) + 1;
    if (length >= sizeof(parent)) {
        return ENAMETOOLONG;
    }
    memcpy(parent, path, length);
    parent[length] = 0;
    int dir = open(parent, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (dir < 0) {
        return errno;
    }
    struct statfs mount;
    struct stat directory, database;
    int error = EACCES;
    if (fstatfs(dir, &mount) || fstat(dir, &directory)
        || fstatat(dir, name + 1, &database, AT_SYMLINK_NOFOLLOW) || !S_ISREG(database.st_mode)) {
        goto done;
    }
    if (!(mount.f_flags & MNT_LOCAL)) {
        error = 0;
        goto done;
    }
    // FileDL reopens the path itself. Only its owner or root may replace entries
    // in the directory while we hand the validated lock over to FileDL.
    if ((directory.st_uid != geteuid() && directory.st_uid != 0)
        || (directory.st_mode & (S_IWGRP | S_IWOTH))) {
        goto done;
    }
    char lock[12];
    fileLockName(name + 1, lock);
    int flags = O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK;
    int fd = openat(dir, lock, flags);
    if (fd < 0 && errno == ENOENT) {
        fd = openat(dir, lock, flags | O_CREAT, database.st_mode & 0666);
    }
    if (fd < 0) {
        error = errno;
        goto done;
    }
    if (regularFile(fd)) {
        int locked = 0;
        if (lookup) {
            // The caller can already own a different database's writer lock.
            locked = flock(fd, LOCK_SH | LOCK_NB);
            if (locked != 0) {
                error = errno;
            }
        }
        struct stat pinned, named;
        if (!locked && !fstat(fd, &pinned) && !fstatat(dir, lock, &named, AT_SYMLINK_NOFOLLOW)
            && pinned.st_nlink == 1 && pinned.st_dev == named.st_dev && pinned.st_ino == named.st_ino) {
            *result = fd;
            error = 0;
        }
    }
    if (*result < 0) {
        close(fd);
    }
done:
    close(dir);
    return error;
}

int TimesCheckWriterLock(int fd)
{
    if (!regularFile(fd)) {
        return EINVAL;
    }
    int result = flock(fd, LOCK_SH | LOCK_NB);
    if (result == 0) {
        flock(fd, LOCK_UN);
        return EINVAL;
    }
    return errno == EWOULDBLOCK ? 0 : errno;
}
