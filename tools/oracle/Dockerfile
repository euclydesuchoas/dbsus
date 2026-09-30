# Reference oracle for DBC -> DBF conversion, used only during development to produce expected
# hashes. pyreaddbc (AGPL-3.0) runs here as a black box; it is never a dependency of DbSus and its
# source is not used as a basis for DbSus code.
FROM python:3.13-slim

RUN pip install --no-cache-dir pyreaddbc==2.0.4

COPY oracle.py /oracle/oracle.py

ENTRYPOINT ["python", "/oracle/oracle.py"]
