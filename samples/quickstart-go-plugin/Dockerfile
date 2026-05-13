FROM golang:1.22-alpine AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN CGO_ENABLED=0 go build -ldflags="-s -w" -o /out/plugin ./...

FROM scratch
COPY --from=build /out/plugin /plugin
USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["/plugin"]
