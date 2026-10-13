FROM python:3.12-slim
WORKDIR /app
COPY main.py .
COPY static ./static
ENV COREBUSY_HUB_DATA=/data
VOLUME /data
EXPOSE 8400
CMD ["python", "main.py"]
