/* Support for ahead-of-time compiled Brainfuck: see bfrt.h. */
#include "bfrt.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int bf_context_init(bf_context *context, bf_read_fn read, bf_write_fn write, void *io, int64_t budget)
{
    memset(context, 0, sizeof(*context));
    if (!read || !write || budget <= 0) {
        return 0;
    }

    context->tape = (int32_t *)calloc(BF_TAPE_LENGTH, sizeof(int32_t));
    if (!context->tape) {
        return 0;
    }

    context->read = read;
    context->write = write;
    context->io = io;
    context->budget = budget;
    context->fuel = budget;
    return 1;
}

void bf_context_free(bf_context *context)
{
    free(context->tape);
    context->tape = NULL;
}

int64_t bf_context_iterations(const bf_context *context)
{
    return context->total + (context->budget - context->fuel);
}

static int fault(bf_context *c, const char *message)
{
    snprintf(c->fault, sizeof(c->fault), "%s", message);
    return BFX_FAULT;
}

int bf_in(bf_context *c, int32_t *cell)
{
    int64_t used = c->budget - c->fuel;
    int value;
    if (used > c->peak) {
        c->peak = used;
    }

    c->total += used;
    c->fuel = c->budget;
    value = c->read(c->io);
    if (value < 0) {
        return BFX_STOP;
    }

    if (value > 255) {
        return fault(c, "The host supplied an input value that is not a byte.");
    }

    *cell = value;
    return 0;
}

int bf_out(bf_context *c, int32_t value)
{
    if ((uint32_t)value > 255) {
        snprintf(c->fault, sizeof(c->fault), "The Brainfuck program tried to output %d, which is not a byte.", (int)value);
        return BFX_FAULT;
    }

    if (c->write(c->io, (unsigned char)value) != 0) {
        return fault(c, "The bridge refused the program's output.");
    }

    return 0;
}

int bf_fault_overflow(bf_context *c)
{
    return fault(c, "A Brainfuck cell overflowed.");
}

int bf_fault_tape(bf_context *c)
{
    return fault(c, "The Brainfuck program moved outside its tape.");
}

int bf_fault_budget(bf_context *c)
{
    snprintf(c->fault, sizeof(c->fault), "The Brainfuck program ran more than %lld loop iterations without reading input.", (long long)c->budget);
    return BFX_FAULT;
}

static int fits(int64_t value)
{
    return value >= INT32_MIN && value <= INT32_MAX;
}

static int in_tape(const int *index, int count)
{
    int item;
    for (item = 0; item < count; item++) {
        if ((unsigned int)index[item] >= BF_TAPE_LENGTH) {
            return 0;
        }
    }

    return 1;
}

/* a[b[-f+g+b]g[-b+g]g+f[a-b-m+g-f[-]]g[a[-s+a]g-]a] */
int bf_idiom_race(int32_t *t, int a, int b, int f, int g, int m, int s)
{
    int index[6];
    int64_t va, vb, vm, vs;
    index[0] = a; index[1] = b; index[2] = f; index[3] = g; index[4] = m; index[5] = s;
    if (!in_tape(index, 6)) {
        return 0;
    }

    va = t[a];
    vb = t[b];
    vm = t[m];
    vs = t[s];
    if (va <= 0 || vb < 0 || t[f] != 0 || t[g] != 0) {
        return 0;
    }

    if (va > vb) {
        vm += vb;
        vs += va - vb;
        vb = 0;
    } else {
        vm += va;
        vb -= va;
    }

    if (!fits(vm) || !fits(vs)) {
        return 0;
    }

    t[a] = 0;
    t[b] = (int32_t)vb;
    t[m] = (int32_t)vm;
    t[s] = (int32_t)vs;
    return 1;
}

/* n[n-r+c-c[-t+e+c]e[-c+e]e+t[e-t[-]]e[q+r[-c+r]e-]n] */
int bf_idiom_divmod(int32_t *t, int n, int r, int c, int u, int e, int q)
{
    int index[6];
    int64_t vn, vr, vc, vq, divisor, total;
    index[0] = n; index[1] = r; index[2] = c; index[3] = u; index[4] = e; index[5] = q;
    if (!in_tape(index, 6)) {
        return 0;
    }

    vn = t[n];
    vr = t[r];
    vc = t[c];
    vq = t[q];
    if (vn <= 0 || vr < 0 || vc < 1 || t[u] != 0 || t[e] != 0) {
        return 0;
    }

    divisor = vr + vc;
    total = vr + vn;
    vr = total % divisor;
    vc = divisor - vr;
    vq += total / divisor;
    if (!fits(vr) || !fits(vc) || !fits(vq)) {
        return 0;
    }

    t[n] = 0;
    t[r] = (int32_t)vr;
    t[c] = (int32_t)vc;
    t[q] = (int32_t)vq;
    return 1;
}

/* a[-b[-p+t+b]t[-b+t]a] */
int bf_idiom_multiply(int32_t *t, int a, int b, int p, int u)
{
    int index[4];
    int64_t va, vb, vp;
    index[0] = a; index[1] = b; index[2] = p; index[3] = u;
    if (!in_tape(index, 4)) {
        return 0;
    }

    va = t[a];
    vb = t[b];
    vp = t[p];
    if (va <= 0 || vb < 0 || t[u] != 0) {
        return 0;
    }

    vp += va * vb;
    if (!fits(vp)) {
        return 0;
    }

    t[a] = 0;
    t[p] = (int32_t)vp;
    return 1;
}
