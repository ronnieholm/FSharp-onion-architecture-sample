import http from 'k6/http';
import { check } from 'k6';

// From the command-line, run as follows:
//
// k6 run --no-usage-report loadTest.js
//
// or
//
// while true; do k6 run --no-usage-report loadTest.js; done
//
// While k6 is running in an infinite loop, tweak parameters and they're picked
// up in the next run. It's a more dynamic version of the ramping-vus executor
// and with per k6 invocation reports.

export const options = {
    noConnectionReuse: false,
    insecureSkipTLSVerify: true,
    scenarios: {
        getStoryById: {
            executor: 'constant-vus',
            //exec: 'baseline',
            exec: 'getStoryById',
            vus: 10,
            duration: '10s'
        }
    }
};

// Acquire an access token before running any tests.
const url = 'https://localhost:5000';
const token = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlcyI6WyJtZW1iZXIiLCJhZG1pbiJdLCJqdGkiOiJjZTllYzIwYy05MGUwLTQ2OTAtYTdhNi04Yjg4ZjA4YTVmOTQiLCJ1c2VySWQiOiIxIiwiZXhwIjoxNjk3OTcwOTYzLCJpc3MiOiJodHRwczovL3NjcnVtLWRldi8iLCJhdWQiOiJodHRwczovL3NjcnVtLWRldi8ifQ.4DtPo6BnNUb9iZOkB1JUBQVIJzKQT6WJJWEqzTBo-6w';

export function baseline() {
    const res = http.get(`${url}/tests/current-time`, {
        headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${token}`
        }
    });

    check(res, {
        'is status 200': (r) => r.status === 200
    });
}

// Create a story before running this test.
const id = '729e9f44-dc7f-42bd-bfbc-feda885b874a'

export function getStoryById() {
    const res = http.get(`${url}/stories/${id}`, {
        headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${token}`
        }
    });

    check(res, {
        'is status 200': (r) => r.status === 200
    });
}
