from . import cli
import sys


if __name__ == "__main__":
    try:
        raise SystemExit(cli.main())
    except RuntimeError as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
