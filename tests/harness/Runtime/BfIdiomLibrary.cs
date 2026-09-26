namespace G29.Bridge.Runtime
{
    // Loop shapes that occur in src/brainfuck/g29-main.bf, with their closed forms.
    // Patterns must match the macros exactly (cells named by first appearance);
    // RuntimeTests proves each closed form against plain execution.
    internal static class BfIdiomLibrary
    {
        internal static void RegisterAll()
        {
            // race(a, b, m): m += min(a, b); a -= min; b -= min (a's excess parks in s
            // and is moved back after the loop).
            BfIdioms.Register(new BfIdioms.Idiom(
                "race",
                "a[b[-f+g+b]g[-b+g]g+f[a-b-m+g-f[-]]g[a[-s+a]g-]a]",
                delegate(long[] v)
                {
                    long a = v[0], b = v[1], f = v[2], g = v[3];
                    if (a <= 0 || b < 0 || f != 0 || g != 0)
                    {
                        return false;
                    }

                    if (a > b)
                    {
                        v[4] += b;
                        v[5] += a - b;
                        v[1] = 0;
                    }
                    else
                    {
                        v[4] += a;
                        v[1] = b - a;
                    }

                    v[0] = 0;
                    return true;
                }));

            // divmod(n, d, q, r): each step moves one unit from n to r and counts c
            // (= d - r) down; when c reaches zero q grows and r refills c.
            BfIdioms.Register(new BfIdioms.Idiom(
                "divmod",
                "n[n-r+c-c[-t+e+c]e[-c+e]e+t[e-t[-]]e[q+r[-c+r]e-]n]",
                delegate(long[] v)
                {
                    long n = v[0], r = v[1], c = v[2], t = v[3], e = v[4];
                    if (n <= 0 || r < 0 || c < 1 || t != 0 || e != 0)
                    {
                        return false;
                    }

                    long d = r + c;
                    long total = r + n;
                    v[0] = 0;
                    v[1] = total % d;
                    v[2] = d - v[1];
                    v[5] += total / d;
                    return true;
                }));

            // mul(a, b, p): p += a * b, b restored through t each step.
            BfIdioms.Register(new BfIdioms.Idiom(
                "multiply",
                "a[-b[-p+t+b]t[-b+t]a]",
                delegate(long[] v)
                {
                    long a = v[0], b = v[1], t = v[3];
                    if (a <= 0 || b < 0 || t != 0)
                    {
                        return false;
                    }

                    v[2] += a * b;
                    v[0] = 0;
                    return true;
                }));
        }
    }
}
