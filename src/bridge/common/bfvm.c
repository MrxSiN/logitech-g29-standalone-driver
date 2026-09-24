/* BF32-G29 virtual machine: see bfvm.h. */
#include "bfvm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

enum {
    OP_ADD = 0,
    OP_MOVE = 1,
    OP_OPEN = 2,
    OP_CLOSE = 3,
    OP_READ = 4,
    OP_WRITE = 5,
    OP_CLEAR = 6,
    OP_MULADD = 7,
    OP_IDIOM = 8
};

#define MAX_IDIOM_CELLS 8
#define MAX_IDIOM_STEPS 64

/* ------------------------------------------------------------------------- */
/* Idioms (BfIdioms.cs, BfIdiomLibrary.cs)                                    */
/* ------------------------------------------------------------------------- */

typedef int (*idiom_eval)(int64_t *values);

typedef struct {
    unsigned char op;
    int cell;
    int argument;
} idiom_step;

typedef struct {
    const char *name;
    const char *pattern;
    idiom_eval evaluate;
    int cell_count;
    int step_count;
    idiom_step steps[MAX_IDIOM_STEPS];
} idiom;

typedef struct {
    int idiom;
    int cell_count;
    int offsets[MAX_IDIOM_CELLS];
} idiom_match;

/* race(a, b, m): m += min(a, b); a -= min; b -= min (a's excess parks in s). */
static int eval_race(int64_t *v)
{
    int64_t a = v[0], b = v[1], f = v[2], g = v[3];
    if (a <= 0 || b < 0 || f != 0 || g != 0) {
        return 0;
    }

    if (a > b) {
        v[4] += b;
        v[5] += a - b;
        v[1] = 0;
    } else {
        v[4] += a;
        v[1] = b - a;
    }

    v[0] = 0;
    return 1;
}

/* divmod(n, d, q, r) */
static int eval_divmod(int64_t *v)
{
    int64_t n = v[0], r = v[1], c = v[2], t = v[3], e = v[4];
    int64_t d, total;
    if (n <= 0 || r < 0 || c < 1 || t != 0 || e != 0) {
        return 0;
    }

    d = r + c;
    total = r + n;
    v[0] = 0;
    v[1] = total % d;
    v[2] = d - v[1];
    v[5] += total / d;
    return 1;
}

/* mul(a, b, p): p += a * b */
static int eval_multiply(int64_t *v)
{
    int64_t a = v[0], b = v[1], t = v[3];
    if (a <= 0 || b < 0 || t != 0) {
        return 0;
    }

    v[2] += a * b;
    v[0] = 0;
    return 1;
}

static idiom known_idioms[] = {
    { "race", "a[b[-f+g+b]g[-b+g]g+f[a-b-m+g-f[-]]g[a[-s+a]g-]a]", eval_race, 0, 0, { { 0 } } },
    { "divmod", "n[n-r+c-c[-t+e+c]e[-c+e]e+t[e-t[-]]e[q+r[-c+r]e-]n]", eval_divmod, 0, 0, { { 0 } } },
    { "multiply", "a[-b[-p+t+b]t[-b+t]a]", eval_multiply, 0, 0, { { 0 } } }
};

#define IDIOM_COUNT ((int)(sizeof(known_idioms) / sizeof(known_idioms[0])))

static void idiom_compile(idiom *item)
{
    char names[MAX_IDIOM_CELLS];
    int cell = -1;
    const char *c;
    item->cell_count = 0;
    item->step_count = 0;
    for (c = item->pattern; *c; c++) {
        unsigned char op;
        int argument = 0;
        if (*c >= 'a' && *c <= 'z') {
            int index;
            for (index = 0; index < item->cell_count && names[index] != *c; index++) {
            }

            if (index == item->cell_count) {
                names[item->cell_count++] = *c;
            }

            cell = index;
            continue;
        }

        switch (*c) {
        case '+': op = OP_ADD; argument = 1; break;
        case '-': op = OP_ADD; argument = -1; break;
        case '[': op = OP_OPEN; break;
        default: op = OP_CLOSE; break;
        }

        if (op == OP_ADD && item->step_count > 0 &&
            item->steps[item->step_count - 1].op == OP_ADD &&
            item->steps[item->step_count - 1].cell == cell) {
            item->steps[item->step_count - 1].argument += argument;
            continue;
        }

        item->steps[item->step_count].op = op;
        item->steps[item->step_count].cell = cell;
        item->steps[item->step_count].argument = argument;
        item->step_count++;
    }
}

/* Returns 1 and fills offsets when the compiled loop open..close is the idiom. */
static int idiom_bind(const idiom *item, const unsigned char *ops, const int *args, int open, int close, int *offsets)
{
    int known[MAX_IDIOM_CELLS];
    int index = open;
    int position = 0;
    int s, other;
    for (s = 0; s < item->cell_count; s++) {
        known[s] = 0;
    }

    for (s = 0; s < item->step_count; s++) {
        const idiom_step *step = &item->steps[s];
        unsigned char actual;
        if (index > close) {
            return 0;
        }

        if (!known[step->cell]) {
            if (ops[index] == OP_MOVE) {
                position += args[index];
                index++;
            }

            for (other = 0; other < item->cell_count; other++) {
                if (known[other] && offsets[other] == position) {
                    return 0;
                }
            }

            offsets[step->cell] = position;
            known[step->cell] = 1;
        } else if (offsets[step->cell] != position) {
            if (ops[index] != OP_MOVE || position + args[index] != offsets[step->cell]) {
                return 0;
            }

            position += args[index];
            index++;
        }

        if (index > close) {
            return 0;
        }

        actual = ops[index];
        if (step->op == OP_ADD) {
            if (actual != OP_ADD || args[index] != step->argument) {
                return 0;
            }
        } else if (step->op == OP_OPEN) {
            if (actual != OP_OPEN && index != open) {
                return 0;
            }
        } else if (actual != step->op) {
            return 0;
        }

        index++;
    }

    return index == close + 1;
}

/* ------------------------------------------------------------------------- */
/* Program                                                                    */
/* ------------------------------------------------------------------------- */

struct bf_program {
    unsigned char *ops;
    int *args;
    int *loop_ends;
    size_t length;
    /* multiply-add loops: offsets and factors, [0] is the counter step */
    int **muladd_offsets;
    int **muladd_factors;
    int *muladd_sizes;
    size_t muladd_count;
    idiom_match *idioms;
    size_t idiom_count;
};

typedef struct {
    void *data;
    size_t count;
    size_t capacity;
    size_t item;
} vec;

static int vec_push(vec *v, const void *item)
{
    if (v->count == v->capacity) {
        size_t capacity = v->capacity ? v->capacity * 2 : 1024;
        void *data = realloc(v->data, capacity * v->item);
        if (!data) {
            return 0;
        }

        v->data = data;
        v->capacity = capacity;
    }

    memcpy((char *)v->data + v->count * v->item, item, v->item);
    v->count++;
    return 1;
}

static void set_error(char *error, size_t length, const char *text)
{
    if (error && length) {
        strncpy(error, text, length - 1);
        error[length - 1] = 0;
    }
}

static int try_muladd(bf_program *program, vec *offsets_list, vec *factors_list, vec *sizes, int open, int close)
{
    /* insertion-ordered offset -> factor map, like the C# Dictionary */
    int keys[256];
    int64_t values[256];
    int count = 0, offset = 0, index, item;
    int64_t step = 0;
    int found = 0;
    int *offsets, *factors, size;
    for (index = open + 1; index < close; index++) {
        switch (program->ops[index]) {
        case OP_ADD:
            for (item = 0; item < count && keys[item] != offset; item++) {
            }

            if (item == count) {
                if (count == 256) {
                    return 1;
                }

                keys[count] = offset;
                values[count] = 0;
                count++;
            }

            values[item] += program->args[index];
            break;
        case OP_MOVE:
            offset += program->args[index];
            break;
        default:
            return 1;
        }
    }

    for (item = 0; item < count; item++) {
        if (keys[item] == 0) {
            step = values[item];
            found = 1;
        }
    }

    if (offset != 0 || !found || (step != 1 && step != -1)) {
        return 1;
    }

    size = 1;
    for (item = 0; item < count; item++) {
        if (keys[item] == 0 || values[item] == 0) {
            continue;
        }

        if (values[item] < INT32_MIN || values[item] > INT32_MAX) {
            return 1;
        }

        size++;
    }

    offsets = (int *)malloc(sizeof(int) * size);
    factors = (int *)malloc(sizeof(int) * size);
    if (!offsets || !factors) {
        free(offsets);
        free(factors);
        return 0;
    }

    offsets[0] = 0;
    factors[0] = (int)step;
    size = 1;
    for (item = 0; item < count; item++) {
        if (keys[item] == 0 || values[item] == 0) {
            continue;
        }

        offsets[size] = keys[item];
        factors[size] = (int)values[item];
        size++;
    }

    if (!vec_push(offsets_list, &offsets) || !vec_push(factors_list, &factors) || !vec_push(sizes, &size)) {
        free(offsets);
        free(factors);
        return 0;
    }

    program->ops[open] = OP_MULADD;
    program->args[open] = (int)(offsets_list->count - 1);
    return 1;
}

static int optimize(bf_program *program)
{
    vec matches = { 0, 0, 0, sizeof(idiom_match) };
    vec offsets_list = { 0, 0, 0, sizeof(int *) };
    vec factors_list = { 0, 0, 0, sizeof(int *) };
    vec sizes = { 0, 0, 0, sizeof(int) };
    size_t open;
    int i;
    static int compiled = 0;
    if (!compiled) {
        for (i = 0; i < IDIOM_COUNT; i++) {
            idiom_compile(&known_idioms[i]);
        }

        compiled = 1;
    }

    for (open = 0; open < program->length; open++) {
        int close, matched = 0;
        if (program->ops[open] != OP_OPEN) {
            continue;
        }

        close = program->args[open];
        for (i = 0; i < IDIOM_COUNT && !matched; i++) {
            idiom_match match;
            if (idiom_bind(&known_idioms[i], program->ops, program->args, (int)open, close, match.offsets)) {
                match.idiom = i;
                match.cell_count = known_idioms[i].cell_count;
                if (!vec_push(&matches, &match)) {
                    return 0;
                }

                program->ops[open] = OP_IDIOM;
                program->args[open] = (int)(matches.count - 1);
                matched = 1;
            }
        }

        if (matched) {
            continue;
        }

        if (close == (int)open + 2 && program->ops[open + 1] == OP_ADD && (program->args[open + 1] == 1 || program->args[open + 1] == -1)) {
            program->ops[open] = OP_CLEAR;
            program->args[open] = program->args[open + 1];
            continue;
        }

        if (!try_muladd(program, &offsets_list, &factors_list, &sizes, (int)open, close)) {
            return 0;
        }
    }

    program->idioms = (idiom_match *)matches.data;
    program->idiom_count = matches.count;
    program->muladd_offsets = (int **)offsets_list.data;
    program->muladd_factors = (int **)factors_list.data;
    program->muladd_sizes = (int *)sizes.data;
    program->muladd_count = offsets_list.count;
    return 1;
}

bf_program *bf_compile(const char *source, size_t length, int optimize_loops, char *error, size_t error_length)
{
    vec ops = { 0, 0, 0, sizeof(unsigned char) };
    vec args = { 0, 0, 0, sizeof(int) };
    vec open = { 0, 0, 0, sizeof(int) };
    bf_program *program;
    size_t index;
    for (index = 0; index < length; index++) {
        char command = source[index];
        size_t last = ops.count;
        unsigned char *op_data = (unsigned char *)ops.data;
        int *arg_data = (int *)args.data;
        switch (command) {
        case '+':
        case '-':
        case '>':
        case '<': {
            unsigned char kind = (command == '+' || command == '-') ? OP_ADD : OP_MOVE;
            int delta = (command == '+' || command == '>') ? 1 : -1;
            if (last > 0 && op_data[last - 1] == kind) {
                arg_data[last - 1] += delta;
                if (arg_data[last - 1] == 0) {
                    ops.count--;
                    args.count--;
                }
            } else {
                if (!vec_push(&ops, &kind) || !vec_push(&args, &delta)) {
                    goto out_of_memory;
                }
            }

            break;
        }

        case '[': {
            unsigned char kind = OP_OPEN;
            int position = (int)ops.count, zero = 0;
            if (!vec_push(&open, &position) || !vec_push(&ops, &kind) || !vec_push(&args, &zero)) {
                goto out_of_memory;
            }

            break;
        }

        case ']': {
            unsigned char kind = OP_CLOSE;
            int start;
            if (open.count == 0) {
                set_error(error, error_length, "The Brainfuck program has an unmatched ']'.");
                goto failed;
            }

            start = ((int *)open.data)[--open.count];
            ((int *)args.data)[start] = (int)ops.count;
            if (!vec_push(&ops, &kind) || !vec_push(&args, &start)) {
                goto out_of_memory;
            }

            break;
        }

        case ',':
        case '.': {
            unsigned char kind = command == ',' ? OP_READ : OP_WRITE;
            int zero = 0;
            if (!vec_push(&ops, &kind) || !vec_push(&args, &zero)) {
                goto out_of_memory;
            }

            break;
        }

        default:
            break;
        }
    }

    if (open.count != 0) {
        set_error(error, error_length, "The Brainfuck program has an unmatched '['.");
        goto failed;
    }

    program = (bf_program *)calloc(1, sizeof(bf_program));
    if (!program) {
        goto out_of_memory;
    }

    program->ops = (unsigned char *)ops.data;
    program->args = (int *)args.data;
    program->length = ops.count;
    program->loop_ends = (int *)calloc(ops.count ? ops.count : 1, sizeof(int));
    free(open.data);
    if (!program->loop_ends) {
        bf_program_free(program);
        set_error(error, error_length, "Out of memory.");
        return NULL;
    }

    for (index = 0; index < program->length; index++) {
        if (program->ops[index] == OP_OPEN) {
            program->loop_ends[index] = program->args[index];
        }
    }

    if (optimize_loops && !optimize(program)) {
        bf_program_free(program);
        set_error(error, error_length, "Out of memory.");
        return NULL;
    }

    return program;

out_of_memory:
    set_error(error, error_length, "Out of memory.");
failed:
    free(ops.data);
    free(args.data);
    free(open.data);
    return NULL;
}

void bf_program_free(bf_program *program)
{
    size_t index;
    if (!program) {
        return;
    }

    for (index = 0; index < program->muladd_count; index++) {
        free(program->muladd_offsets[index]);
        free(program->muladd_factors[index]);
    }

    free(program->muladd_offsets);
    free(program->muladd_factors);
    free(program->muladd_sizes);
    free(program->idioms);
    free(program->ops);
    free(program->args);
    free(program->loop_ends);
    free(program);
}

size_t bf_program_length(const bf_program *program)
{
    return program->length;
}

size_t bf_program_idiom_count(const bf_program *program)
{
    return program->idiom_count;
}

size_t bf_program_muladd_count(const bf_program *program)
{
    return program->muladd_count;
}

/* ------------------------------------------------------------------------- */
/* Execution                                                                  */
/* ------------------------------------------------------------------------- */

struct bf_vm {
    const bf_program *program;
    int32_t *tape;
    bf_read_fn read;
    bf_write_fn write;
    void *context;
    int64_t step_budget;
    int pointer;
    size_t counter;
    int64_t total_steps;
    int64_t steps_since_read;
};

bf_vm *bf_vm_create(const bf_program *program, bf_read_fn read, bf_write_fn write, void *context, int64_t step_budget)
{
    bf_vm *vm;
    if (!program || !read || !write || step_budget <= 0) {
        return NULL;
    }

    vm = (bf_vm *)calloc(1, sizeof(bf_vm));
    if (!vm) {
        return NULL;
    }

    vm->tape = (int32_t *)calloc(BF_TAPE_LENGTH, sizeof(int32_t));
    if (!vm->tape) {
        free(vm);
        return NULL;
    }

    vm->program = program;
    vm->read = read;
    vm->write = write;
    vm->context = context;
    vm->step_budget = step_budget;
    return vm;
}

void bf_vm_free(bf_vm *vm)
{
    if (vm) {
        free(vm->tape);
        free(vm);
    }
}

int64_t bf_vm_total_steps(const bf_vm *vm)
{
    return vm->total_steps;
}

int bf_vm_pointer(const bf_vm *vm)
{
    return vm->pointer;
}

int32_t bf_vm_cell(const bf_vm *vm, int index)
{
    return vm->tape[index];
}

void bf_vm_set_cell(bf_vm *vm, int index, int32_t value)
{
    vm->tape[index] = value;
}

/* Closed form of an idiom; 0 when its preconditions do not hold. */
static int idiom_execute(const idiom_match *match, int32_t *cells, int p)
{
    int64_t values[MAX_IDIOM_CELLS];
    int index;
    for (index = 0; index < match->cell_count; index++) {
        unsigned int target = (unsigned int)(p + match->offsets[index]);
        if (target >= BF_TAPE_LENGTH) {
            return 0;
        }

        values[index] = cells[target];
    }

    if (!known_idioms[match->idiom].evaluate(values)) {
        return 0;
    }

    for (index = 0; index < match->cell_count; index++) {
        if (values[index] < INT32_MIN || values[index] > INT32_MAX) {
            return 0;
        }
    }

    for (index = 0; index < match->cell_count; index++) {
        cells[p + match->offsets[index]] = (int32_t)values[index];
    }

    return 1;
}

#define FAULT(text) do { message = (text); goto fault; } while (0)

int bf_vm_run(bf_vm *vm, char *fault, size_t fault_length)
{
    const bf_program *program = vm->program;
    const unsigned char *ops = program->ops;
    const int *args = program->args;
    size_t length = program->length;
    int32_t *cells = vm->tape;
    int p = vm->pointer;
    size_t pc = vm->counter;
    int64_t steps = vm->steps_since_read;
    int64_t budget = vm->step_budget;
    const char *message = NULL;
    char buffer[160];
    while (pc < length) {
        int argument;
        if (++steps > budget) {
            snprintf(buffer, sizeof(buffer), "The Brainfuck program ran more than %lld steps without reading input.", (long long)budget);
            FAULT(buffer);
        }

        argument = args[pc];
        switch (ops[pc]) {
        case OP_ADD: {
            int64_t value = (int64_t)cells[p] + argument;
            if (value < INT32_MIN || value > INT32_MAX) {
                FAULT("A Brainfuck cell overflowed.");
            }

            cells[p] = (int32_t)value;
            break;
        }

        case OP_MOVE:
            p += argument;
            if ((unsigned int)p >= BF_TAPE_LENGTH) {
                FAULT("The Brainfuck program moved outside its tape.");
            }

            break;
        case OP_OPEN:
            if (cells[p] == 0) {
                pc = (size_t)argument;
            }

            break;
        case OP_CLOSE:
            if (cells[p] != 0) {
                pc = (size_t)argument;
            }

            break;
        case OP_READ: {
            int value;
            vm->pointer = p;
            vm->counter = pc;
            vm->total_steps += steps;
            steps = 0;
            value = vm->read(vm->context);
            if (value < 0) {
                vm->steps_since_read = 0;
                return BF_STOPPED;
            }

            if (value > 255) {
                FAULT("The host supplied an input value that is not a byte.");
            }

            cells[p] = value;
            break;
        }

        case OP_WRITE: {
            int32_t value = cells[p];
            if ((uint32_t)value > 255) {
                snprintf(buffer, sizeof(buffer), "The Brainfuck program tried to output %d, which is not a byte.", (int)value);
                FAULT(buffer);
            }

            vm->pointer = p;
            vm->counter = pc;
            if (vm->write(vm->context, (unsigned char)value) != 0) {
                FAULT("The bridge refused the program's output.");
            }

            break;
        }

        case OP_CLEAR: {
            int32_t value = cells[p];
            if (argument < 0 ? value < 0 : value > 0) {
                FAULT("A Brainfuck cell overflowed.");
            }

            cells[p] = 0;
            pc = (size_t)program->loop_ends[pc];
            break;
        }

        case OP_MULADD: {
            if (cells[p] != 0) {
                const int *offsets = program->muladd_offsets[argument];
                const int *factors = program->muladd_factors[argument];
                int size = program->muladd_sizes[argument];
                int step = factors[0];
                int64_t count = cells[p];
                int item;
                if ((step < 0) != (count > 0)) {
                    FAULT("A Brainfuck cell overflowed.");
                }

                if (count < 0) {
                    count = -count;
                }

                for (item = 1; item < size; item++) {
                    int target = p + offsets[item];
                    int64_t value;
                    if ((unsigned int)target >= BF_TAPE_LENGTH) {
                        FAULT("The Brainfuck program moved outside its tape.");
                    }

                    value = (int64_t)cells[target] + count * factors[item];
                    if (value < INT32_MIN || value > INT32_MAX) {
                        FAULT("A Brainfuck cell overflowed.");
                    }

                    cells[target] = (int32_t)value;
                }

                cells[p] = 0;
            }

            pc = (size_t)program->loop_ends[pc];
            break;
        }

        case OP_IDIOM:
            if (idiom_execute(&program->idioms[argument], cells, p)) {
                pc = (size_t)program->loop_ends[pc];
            } else if (cells[p] == 0) {
                pc = (size_t)program->loop_ends[pc];
            }

            /* otherwise the preconditions failed: run the loop body plainly */
            break;
        }

        pc++;
    }

    vm->pointer = p;
    vm->counter = pc;
    vm->total_steps += steps;
    vm->steps_since_read = steps;
    return BF_FINISHED;

fault:
    vm->pointer = p;
    vm->counter = pc;
    vm->total_steps += steps;
    vm->steps_since_read = steps;
    set_error(fault, fault_length, message);
    return BF_FAULT;
}
