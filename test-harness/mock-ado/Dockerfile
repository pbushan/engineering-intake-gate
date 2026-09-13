FROM python:3.13-alpine
WORKDIR /mock
COPY server.py ./server.py
USER nobody
EXPOSE 8081
ENTRYPOINT ["python", "server.py"]
