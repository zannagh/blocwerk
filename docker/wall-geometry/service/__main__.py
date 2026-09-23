"""`python -m service`: serve on HOST:PORT (default 127.0.0.1:8000), refusing an open bind."""
from computejobs.serve import serve

from .settings import settings  # noqa: F401 - loads the env (and scrubs the secrets) first


def main():
    serve("service.main:app", "wall-geometry", 8000)


if __name__ == "__main__":
    main()
