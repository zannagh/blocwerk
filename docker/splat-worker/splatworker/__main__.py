"""`python -m splatworker`: serve on HOST:PORT (default 127.0.0.1:8100), refusing an open bind."""
from computejobs.serve import serve

from .settings import settings  # noqa: F401 - loads the env (and scrubs the secrets) first


def main():
    serve("splatworker.main:app", "splat-worker", 8100)


if __name__ == "__main__":
    main()
