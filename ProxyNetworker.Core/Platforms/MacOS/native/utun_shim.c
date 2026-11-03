#include <fcntl.h>
#include <sys/ioctl.h>
#include <sys/kern_control.h>
#include <sys/uio.h>

_Static_assert(sizeof(struct ctl_info) == 100, "Unexpected ctl_info ABI");
_Static_assert(sizeof(struct sockaddr_ctl) == 32, "Unexpected sockaddr_ctl ABI");
_Static_assert(sizeof(struct iovec) == 16, "Only 64-bit Darwin is supported");

/* Keep Darwin variadic calls in C: arm64 passes their optional arguments on the stack. */
__attribute__((visibility("default")))
int pn_utun_control_info(int descriptor, struct ctl_info *info)
{
    return ioctl(descriptor, CTLIOCGINFO, info);
}

__attribute__((visibility("default")))
int pn_utun_prepare_socket(int descriptor)
{
    int flags = fcntl(descriptor, F_GETFL);
    if (flags < 0 || fcntl(descriptor, F_SETFL, flags | O_NONBLOCK) < 0)
        return -1;

    flags = fcntl(descriptor, F_GETFD);
    if (flags < 0)
        return -1;

    return fcntl(descriptor, F_SETFD, flags | FD_CLOEXEC);
}
