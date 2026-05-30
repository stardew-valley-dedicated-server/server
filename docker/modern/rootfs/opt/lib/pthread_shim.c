/*
 * pthread_shim.c — Provides glibc-internal __pthread_* symbols for musl.
 *
 * steamclient.so references __pthread_key_create and other __pthread_*
 * internal symbols that glibc exports but musl doesn't. gcompat doesn't
 * cover these either. This shim forwards them to the standard POSIX versions.
 *
 * Compile: musl-gcc -shared -fPIC -o pthread_shim.so pthread_shim.c -lpthread
 * Usage:   LD_PRELOAD=/opt/lib/pthread_shim.so ./game
 */

#include <pthread.h>
#include <signal.h>

/* glibc internal symbol aliases */
int __pthread_key_create(pthread_key_t *key, void (*destructor)(void *)) {
    return pthread_key_create(key, destructor);
}

int __pthread_key_delete(pthread_key_t key) {
    return pthread_key_delete(key);
}

int __pthread_once(pthread_once_t *once_control, void (*init_routine)(void)) {
    return pthread_once(once_control, init_routine);
}

int __pthread_mutex_init(pthread_mutex_t *mutex, const pthread_mutexattr_t *attr) {
    return pthread_mutex_init(mutex, attr);
}

int __pthread_mutex_destroy(pthread_mutex_t *mutex) {
    return pthread_mutex_destroy(mutex);
}

int __pthread_mutex_lock(pthread_mutex_t *mutex) {
    return pthread_mutex_lock(mutex);
}

int __pthread_mutex_unlock(pthread_mutex_t *mutex) {
    return pthread_mutex_unlock(mutex);
}

int __pthread_setspecific(pthread_key_t key, const void *value) {
    return pthread_setspecific(key, value);
}

void *__pthread_getspecific(pthread_key_t key) {
    return pthread_getspecific(key);
}

int __pthread_atfork(void (*prepare)(void), void (*parent)(void), void (*child)(void)) {
    return pthread_atfork(prepare, parent, child);
}

/* sigaction alias sometimes needed by glibc-linked libraries */
int __sigaction(int signum, const struct sigaction *act, struct sigaction *oldact) {
    return sigaction(signum, act, oldact);
}
