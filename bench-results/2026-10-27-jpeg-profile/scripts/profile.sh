#!/bin/zsh
# usage: profile.sh <name> <command...>; runs the command, samples it for 15 s
set -u
O=/Users/benediktstebner/RiderProjects/jpeg-profile-work/out
name=$1; shift
"$@" > $O/$name.run.txt 2>&1 &
pid=$!
sleep 1
sample $pid 15 -mayDie -file $O/$name.sample.txt > $O/$name.sampler.log 2>&1
echo "sample exit=$?"
wait $pid; echo "run exit=$?"
